module Lattice.Aggregation.Tests.ProjectRowsTests

open System
open Xunit
open FsCheck
open FsCheck.Xunit
open Lattice.App.Aggregation

let hostA = Guid.NewGuid()
let hostB = Guid.NewGuid()

let att url name host hostId susp noNew share rac total tasks =
    { MasterUrl = url; ProjectName = name; HostId = hostId; HostName = host
      TaskCount = tasks; ResourceShare = share; AvgCredit = rac; TotalCredit = total
      IsSuspended = susp; NoNewTasks = noNew }

[<Fact>]
let ``groups by MasterUrl and sorts by aggregate RAC descending`` () =
    let groups =
        ProjectRows.compute
            [| att "u1" "P1" "a" hostA false false 100.0 5.0 10.0 1
               att "u1" "P1" "b" hostB false false 100.0 9.0 10.0 2
               att "u2" "P2" "a" hostA false false 100.0 1.0 10.0 3 |]
    Assert.Equal(2, groups.Length)
    Assert.Equal("u1", groups[0].MasterUrl)     // 14 RAC before 1
    Assert.Equal(14.0, groups[0].AvgCredit)
    Assert.Equal(20.0, groups[0].TotalCredit)   // Host* sums, never User* (design §Data model)
    Assert.Equal(3, groups[0].TaskCount)

[<Fact>]
let ``uniform share collapses, differing share varies with min-max`` () =
    let uniform = ProjectRows.compute [| att "u" "P" "a" hostA false false 100.0 1.0 1.0 0
                                         att "u" "P" "b" hostB false false 100.0 1.0 1.0 0 |]
    Assert.Equal(UniformShare 100.0, uniform[0].Share)
    let varies = ProjectRows.compute [| att "u" "P" "a" hostA false false 50.0 1.0 1.0 0
                                        att "u" "P" "b" hostB false false 100.0 1.0 1.0 0 |]
    Assert.Equal(VariesShare (50.0, 100.0), varies[0].Share)

[<Fact>]
let ``status tiers: all-same, one deviation, mixed`` () =
    let same = ProjectRows.compute [| att "u" "P" "a" hostA false false 1.0 1.0 1.0 0 |]
    Assert.Equal(AllSame Active, same[0].Status)
    let dev = ProjectRows.compute [| att "u" "P" "a" hostA true false 1.0 1.0 1.0 0
                                     att "u" "P" "b" hostB false false 1.0 1.0 1.0 0 |]
    Assert.Equal(OneDeviation (Suspended, 1, 2), dev[0].Status)
    let mixed = ProjectRows.compute [| att "u" "P" "a" hostA true false 1.0 1.0 1.0 0
                                       att "u" "P" "b" hostB false true 1.0 1.0 1.0 0 |]
    Assert.Equal(MixedStatus (1, 1), mixed[0].Status)

[<Fact>]
let ``suspended wins when both flags set; display name falls back to url`` () =
    let g = ProjectRows.compute [| att "u" "" "a" hostA true true 1.0 1.0 1.0 0 |]
    Assert.Equal(AllSame Suspended, g[0].Status)
    Assert.Equal("u", g[0].DisplayName)

[<Fact>]
let ``duplicate (MasterUrl, host) attachments collapse to the first occurrence`` () =
    // Codex P2 (PR #46 round 4): SnapshotBuilder copies state.Projects into
    // HostSnapshot.Projects WITHOUT dedup (unlike its TryAdd lookup dicts),
    // and get_state is parsed leniently — a malformed reply can carry the
    // same master_url twice. compute must collapse such duplicates (first
    // occurrence wins), or the parent Hosts count double-counts and expansion
    // emits duplicate ChildKey(MasterUrl, hostId) rows, violating
    // Reconcile.diff's unique-key precondition.
    let groups =
        ProjectRows.compute
            [| att "u" "P" "a" hostA false false 100.0 5.0 10.0 1
               att "u" "P" "a" hostA false false 100.0 9.0 20.0 2 |]
    Assert.Equal(1, groups.Length)
    Assert.Equal(1, groups[0].Attachments.Length) // parent Hosts count = 1, not 2
    Assert.Equal(5.0, groups[0].AvgCredit)        // first occurrence kept, no doubled sums
    Assert.Equal(10.0, groups[0].TotalCredit)
    Assert.Equal(1, groups[0].TaskCount)

let attachmentsGen =
    gen {
        let! n = Gen.choose (0, 8)
        let hostIds = [| hostA; hostB; Guid.NewGuid() |]
        let! atts =
            Gen.listOfLength n (gen {
                let! url = Gen.elements [ "u1"; "u2"; "u3" ]
                let! hostIdx = Gen.choose (0, 2)
                let! susp = Arb.generate<bool>
                let! noNew = Arb.generate<bool>
                let! share = Gen.elements [ 50.0; 100.0; 200.0 ]
                let! rac = Gen.choose (0, 100)
                return att url "P" (string hostIdx) hostIds[hostIdx] susp noNew share (float rac) 1.0 1
            })
        // Duplicates (same url, same host) are deliberately generated: key
        // uniqueness is compute's GUARANTEE, not the caller's precondition.
        return atts |> Array.ofList
    }

type ProjectArbs =
    static member Attachments() = Arb.fromGen attachmentsGen

[<Property(Arbitrary = [| typeof<ProjectArbs> |])>]
let ``attachment conservation: groups partition the deduped input`` (atts: ProjectAttachment[]) =
    // First-occurrence semantics: compute collapses duplicate (url, host)
    // attachments, so groups partition the input deduped in original order.
    let groups = ProjectRows.compute atts
    let regrouped = groups |> Array.collect (fun g -> g.Attachments) |> Array.sortBy (fun a -> a.MasterUrl, a.HostId)
    let deduped = atts |> Array.distinctBy (fun a -> a.MasterUrl, a.HostId)
    regrouped = (deduped |> Array.sortBy (fun a -> a.MasterUrl, a.HostId))

[<Property(Arbitrary = [| typeof<ProjectArbs> |])>]
let ``emitted (MasterUrl, HostId) pairs are distinct across groups`` (atts: ProjectAttachment[]) =
    // Reconcile.diff's unique-key precondition, stated on compute's output:
    // ChildKey(MasterUrl, hostId) identity must never repeat.
    let pairs =
        ProjectRows.compute atts
        |> Array.collect (fun g -> g.Attachments |> Array.map (fun a -> g.MasterUrl, a.HostId))
    pairs.Length = (pairs |> Array.distinct |> Array.length)

[<Property(Arbitrary = [| typeof<ProjectArbs> |])>]
let ``credit sums are per-group host sums`` (atts: ProjectAttachment[]) =
    ProjectRows.compute atts
    |> Array.forall (fun g ->
        g.AvgCredit = (g.Attachments |> Array.sumBy (fun a -> a.AvgCredit))
        && g.TotalCredit = (g.Attachments |> Array.sumBy (fun a -> a.TotalCredit)))

[<Property(Arbitrary = [| typeof<ProjectArbs> |])>]
let ``status summary counts are consistent with attachments`` (atts: ProjectAttachment[]) =
    ProjectRows.compute atts
    |> Array.forall (fun g ->
        let statuses = g.Attachments |> Array.map ProjectRows.status
        let suspended = statuses |> Array.filter (fun s -> s = Suspended) |> Array.length
        let noNew = statuses |> Array.filter (fun s -> s = NoNewTasks) |> Array.length
        match g.Status with
        | AllSame s -> statuses |> Array.forall (fun x -> x = s)
        | OneDeviation (Suspended, d, t) -> d = suspended && t = statuses.Length && noNew = 0
        | OneDeviation (NoNewTasks, d, t) -> d = noNew && t = statuses.Length && suspended = 0
        | OneDeviation (Active, _, _) -> false // Active is never the deviation
        | MixedStatus (s, n) -> s = suspended && n = noNew && suspended > 0 && noNew > 0)

// ---------------------------------------------------------------------------
// orderedRows / compareRows / toggleSort / alignToExisting (issue #57, step 1)
// ---------------------------------------------------------------------------

let allColumns = [ ByName; ByHostCount; ByShare; ByAvgCredit; ByTotalCredit; ByStatus ]

let allProjectSorts =
    DefaultSort :: (allColumns |> List.collect (fun c -> [ ColumnSort(c, Ascending); ColumnSort(c, Descending) ]))

let projectSortGen : Gen<ProjectSort> = Gen.elements allProjectSorts

let groupsGen : Gen<ProjectGroup[]> = attachmentsGen |> Gen.map ProjectRows.compute

/// Real group urls plus two urls that never appear in `groups` — the noise
/// exercises orderedRows/alignToExisting-adjacent callers passing expansion
/// state for a group no longer in scope.
let expandedGen (groups: ProjectGroup[]) : Gen<Set<string>> =
    let candidates = (groups |> Array.map (fun g -> g.MasterUrl) |> Array.toList) @ [ "noise1"; "noise2" ]
    gen {
        let! chosen = Gen.subListOf candidates
        return Set.ofList chosen
    }

let parentUrlsOf (rows: RowSlot list) : string list =
    rows
    |> List.choose (function
        | ParentSlot(g, _) -> Some g.MasterUrl
        | ChildSlot _ -> None)

let childPairsOf (rows: RowSlot list) : (string * Guid) list =
    rows
    |> List.choose (function
        | ChildSlot(g, a) -> Some(g.MasterUrl, a.HostId)
        | ParentSlot _ -> None)

/// sort × aggregate × expanded × groups — the general orderedRows case.
type OrderedRowsCase =
    { Sort: ProjectSort
      Aggregate: bool
      Expanded: Set<string>
      Groups: ProjectGroup[] }

let orderedRowsCaseGen =
    gen {
        let! sort = projectSortGen
        let! aggregate = Arb.generate<bool>
        let! groups = groupsGen
        let! expanded = expandedGen groups
        return { Sort = sort; Aggregate = aggregate; Expanded = expanded; Groups = groups }
    }

type OrderedRowsArbs =
    static member Case() = Arb.fromGen orderedRowsCaseGen

/// column × aggregate × expanded × groups — for direction-invariance checks
/// that need the SAME column under both directions.
type ColumnCase =
    { Column: ProjectSortColumn
      Aggregate: bool
      Expanded: Set<string>
      Groups: ProjectGroup[] }

let columnCaseGen =
    gen {
        let! column = Gen.elements allColumns
        let! aggregate = Arb.generate<bool>
        let! groups = groupsGen
        let! expanded = expandedGen groups
        return { Column = column; Aggregate = aggregate; Expanded = expanded; Groups = groups }
    }

type ColumnCaseArbs =
    static member Case() = Arb.fromGen columnCaseGen

/// groups × direction, for column-agnostic direction checks (I7).
type DirectionCase = { Groups: ProjectGroup[]; Direction: SortDirection }

let directionCaseGen =
    gen {
        let! groups = groupsGen
        let! direction = Gen.elements [ Ascending; Descending ]
        return { Groups = groups; Direction = direction }
    }

type DirectionCaseArbs =
    static member Case() = Arb.fromGen directionCaseGen

/// Every group ties on every column (same name/share/RAC/total/status/host
/// count) so only MasterUrl can break a comparison — forces I6.
let tiedGroupsGen : Gen<ProjectGroup[]> =
    gen {
        let! n = Gen.choose (2, 5)
        let! urls = Gen.listOfLength n (Gen.elements [ "u1"; "u2"; "u3"; "u4"; "u5" ])
        let urls = urls |> List.distinct
        let urls = if urls.Length >= 2 then urls else [ "u1"; "u2" ]
        return
            urls
            |> List.mapi (fun i u -> att u "Same" (sprintf "h%d" i) (Guid.NewGuid()) false false 100.0 5.0 1.0 0)
            |> Array.ofList
            |> ProjectRows.compute
    }

type TiedCase = { Groups: ProjectGroup[]; Column: ProjectSortColumn }

let tiedCaseGen =
    gen {
        let! groups = tiedGroupsGen
        let! column = Gen.elements allColumns
        return { Groups = groups; Column = column }
    }

type TiedCaseArbs =
    static member Case() = Arb.fromGen tiedCaseGen

/// sort × the flattened row keys of one groups[] draw, for comparer-law checks.
type TripleCase = { Sort: ProjectSort; Keys: RowSortKey[] }

let rowSortKeysOf (groups: ProjectGroup[]) : RowSortKey[] =
    groups
    |> Array.collect (fun g ->
        Array.append [| ProjectRows.parentKey g |] (g.Attachments |> Array.map (ProjectRows.childKey g)))

let tripleCaseGen =
    gen {
        let! sort = projectSortGen
        let! groups = groupsGen
        return { Sort = sort; Keys = rowSortKeysOf groups }
    }

type TripleCaseArbs =
    static member Case() = Arb.fromGen tripleCaseGen

[<Property(Arbitrary = [| typeof<OrderedRowsArbs> |])>]
let ``I1 parent rows are ordered by compareRows`` (c: OrderedRowsCase) =
    ProjectRows.orderedRows c.Sort c.Aggregate c.Expanded c.Groups
    |> List.choose (function
        | ParentSlot(g, _) -> Some(ProjectRows.parentKey g)
        | ChildSlot _ -> None)
    |> List.pairwise
    |> List.forall (fun (a, b) -> ProjectRows.compareRows c.Sort a b <= 0)

[<Property(Arbitrary = [| typeof<OrderedRowsArbs> |])>]
let ``I2 each group's slots form one contiguous block headed by its parent`` (c: OrderedRowsCase) =
    let rows = ProjectRows.orderedRows c.Sort c.Aggregate c.Expanded c.Groups
    let urlAt =
        rows
        |> List.mapi (fun i slot ->
            match slot with
            | ParentSlot(g, _) -> i, g.MasterUrl
            | ChildSlot(g, _) -> i, g.MasterUrl)
    let urls = urlAt |> List.map snd |> List.distinct
    urls
    |> List.forall (fun u ->
        let idxs = urlAt |> List.choose (fun (i, x) -> if x = u then Some i else None)
        let contiguous = List.max idxs - List.min idxs + 1 = List.length idxs
        let headedByParent =
            match rows.[List.min idxs] with
            | ParentSlot(g, _) -> g.MasterUrl = u
            | ChildSlot _ -> false
        contiguous && headedByParent)

[<Property(Arbitrary = [| typeof<ColumnCaseArbs> |])>]
let ``I3 child order is direction-invariant and matches compute's attachment order`` (c: ColumnCase) =
    let asc = ProjectRows.orderedRows (ColumnSort(c.Column, Ascending)) c.Aggregate c.Expanded c.Groups
    let desc = ProjectRows.orderedRows (ColumnSort(c.Column, Descending)) c.Aggregate c.Expanded c.Groups
    let childrenOfUrl rows url =
        rows
        |> List.choose (function
            | ChildSlot(g, a) when g.MasterUrl = url -> Some a.HostId
            | ChildSlot _
            | ParentSlot _ -> None)
    c.Groups
    |> Array.filter (fun g -> c.Aggregate && c.Expanded.Contains g.MasterUrl)
    |> Array.forall (fun g ->
        let expected = g.Attachments |> Array.map (fun a -> a.HostId) |> Array.toList
        childrenOfUrl asc g.MasterUrl = expected && childrenOfUrl desc g.MasterUrl = expected)

[<Property(Arbitrary = [| typeof<OrderedRowsArbs> |])>]
let ``I4 collapsed groups contribute only their parent`` (c: OrderedRowsCase) =
    let rows = ProjectRows.orderedRows c.Sort c.Aggregate c.Expanded c.Groups
    let childUrls = childPairsOf rows |> List.map fst |> Set.ofList
    c.Groups
    |> Array.filter (fun g -> not (c.Aggregate && c.Expanded.Contains g.MasterUrl))
    |> Array.forall (fun g -> not (childUrls.Contains g.MasterUrl))

[<Property(Arbitrary = [| typeof<OrderedRowsArbs> |])>]
let ``I5 aggregate false yields no child rows regardless of expansion`` (c: OrderedRowsCase) =
    ProjectRows.orderedRows c.Sort false c.Expanded c.Groups
    |> List.forall (function
        | ParentSlot _ -> true
        | ChildSlot _ -> false)

[<Property(Arbitrary = [| typeof<TiedCaseArbs> |])>]
let ``I6 groups tied on every column break on MasterUrl ascending, in both directions`` (c: TiedCase) =
    let expected = c.Groups |> Array.map (fun g -> g.MasterUrl) |> Array.sort |> Array.toList
    let urlsFor direction =
        ProjectRows.orderedRows (ColumnSort(c.Column, direction)) false Set.empty c.Groups |> parentUrlsOf
    urlsFor Ascending = expected && urlsFor Descending = expected

[<Property(Arbitrary = [| typeof<DirectionCaseArbs> |])>]
let ``I7 ByShare orders parents by (ShareMax, ShareMin) lexicographic`` (c: DirectionCase) =
    let bounds =
        ProjectRows.orderedRows (ColumnSort(ByShare, c.Direction)) false Set.empty c.Groups
        |> List.choose (function
            | ParentSlot(g, _) ->
                let k = ProjectRows.groupKey g
                Some(k.ShareMax, k.ShareMin)
            | ChildSlot _ -> None)
    match c.Direction with
    | Ascending -> bounds |> List.pairwise |> List.forall (fun (a, b) -> a <= b)
    | Descending -> bounds |> List.pairwise |> List.forall (fun (a, b) -> a >= b)

[<Property(Arbitrary = [| typeof<OrderedRowsArbs> |])>]
let ``I8 orderedRows is exactly all parents plus exactly the expanded groups' children`` (c: OrderedRowsCase) =
    let rows = ProjectRows.orderedRows c.Sort c.Aggregate c.Expanded c.Groups
    let expectedParents = c.Groups |> Array.map (fun g -> g.MasterUrl) |> Array.toList |> List.sort
    let expectedChildren =
        c.Groups
        |> Array.filter (fun g -> c.Aggregate && c.Expanded.Contains g.MasterUrl)
        |> Array.collect (fun g -> g.Attachments |> Array.map (fun a -> g.MasterUrl, a.HostId))
        |> Array.toList
        |> List.sort
    (parentUrlsOf rows |> List.sort) = expectedParents
    && (childPairsOf rows |> List.sort) = expectedChildren

[<Property(Arbitrary = [| typeof<TripleCaseArbs> |])>]
let ``comparer antisymmetry: sign(cmp a b) = -sign(cmp b a)`` (c: TripleCase) =
    [ for a in c.Keys do
          for b in c.Keys -> a, b ]
    |> List.forall (fun (a, b) -> sign (ProjectRows.compareRows c.Sort a b) = -sign (ProjectRows.compareRows c.Sort b a))

[<Property(Arbitrary = [| typeof<TripleCaseArbs> |])>]
let ``comparer transitivity: cmp a b <= 0 and cmp b c <= 0 implies cmp a c <= 0`` (c: TripleCase) =
    let cmp = ProjectRows.compareRows c.Sort
    [ for a in c.Keys do
          for b in c.Keys do
              for k in c.Keys -> a, b, k ]
    |> List.forall (fun (a, b, k) -> not (cmp a b <= 0 && cmp b k <= 0) || cmp a k <= 0)

[<Property(Arbitrary = [| typeof<OrderedRowsArbs> |])>]
let ``DefaultSort is equivalent to ColumnSort(ByAvgCredit, Descending)`` (c: OrderedRowsCase) =
    let byDefault = ProjectRows.orderedRows DefaultSort c.Aggregate c.Expanded c.Groups
    let byColumn = ProjectRows.orderedRows (ColumnSort(ByAvgCredit, Descending)) c.Aggregate c.Expanded c.Groups
    byDefault = byColumn

// The token family (XAML SortMemberPath ↔ description PropertyPath ↔ click
// router) has exactly one home: columnToken/tryColumnOfToken. Enumerating the
// cases by reflection means a FUTURE column is covered by this test with zero
// edits — the C# sides carry no case list at all (Codex P2, PR #74).
[<Fact>]
let ``columnToken round-trips through tryColumnOfToken for every column case`` () =
    let cases =
        Microsoft.FSharp.Reflection.FSharpType.GetUnionCases typeof<ProjectSortColumn>
        |> Array.map (fun c ->
            Microsoft.FSharp.Reflection.FSharpValue.MakeUnion(c, [||]) :?> ProjectSortColumn)
    Assert.Equal(6, cases.Length) // bump deliberately when a column is added
    for column in cases do
        Assert.Equal(Some column, ProjectRows.tryColumnOfToken (ProjectRows.columnToken column))

[<Fact>]
let ``tryColumnOfToken rejects unknown and null tokens`` () =
    Assert.Equal(None, ProjectRows.tryColumnOfToken "Bogus")
    Assert.Equal(None, ProjectRows.tryColumnOfToken null)

[<Fact>]
let ``flipDirection swaps the two directions`` () =
    Assert.Equal(Descending, ProjectRows.flipDirection Ascending)
    Assert.Equal(Ascending, ProjectRows.flipDirection Descending)

[<Fact>]
let ``toggleSort covers the full transition table`` () =
    Assert.Equal(ColumnSort(ByName, Ascending), ProjectRows.toggleSort ByName DefaultSort)
    Assert.Equal(ColumnSort(ByName, Descending), ProjectRows.toggleSort ByName (ColumnSort(ByName, Ascending)))
    Assert.Equal(ColumnSort(ByName, Ascending), ProjectRows.toggleSort ByName (ColumnSort(ByName, Descending)))
    Assert.Equal(
        ColumnSort(ByHostCount, Ascending), ProjectRows.toggleSort ByHostCount (ColumnSort(ByName, Ascending)))
    Assert.Equal(
        ColumnSort(ByHostCount, Ascending), ProjectRows.toggleSort ByHostCount (ColumnSort(ByName, Descending)))

[<Fact>]
let ``ByShare orders uniform before varying and breaks ties on ShareMin`` () =
    let distinctMax =
        ProjectRows.compute
            [| att "u-uniform" "P" "a" hostA false false 100.0 1.0 1.0 0
               att "u-uniform" "P" "b" hostB false false 100.0 1.0 1.0 0
               att "u-varies" "P" "a" hostA false false 50.0 1.0 1.0 0
               att "u-varies" "P" "b" hostB false false 200.0 1.0 1.0 0 |]
    // u-uniform key (100,100); u-varies key (200,50) — primary ShareMax decides.
    Assert.Equal<string list>(
        [ "u-uniform"; "u-varies" ],
        ProjectRows.orderedRows (ColumnSort(ByShare, Ascending)) false Set.empty distinctMax |> parentUrlsOf)

    let tiedMax =
        ProjectRows.compute
            [| att "u-a" "P" "a" hostA false false 50.0 1.0 1.0 0
               att "u-a" "P" "b" hostB false false 200.0 1.0 1.0 0
               att "u-b" "P" "a" hostA false false 200.0 1.0 1.0 0
               att "u-b" "P" "b" hostB false false 200.0 1.0 1.0 0 |]
    // u-a: VariesShare(50,200) -> key (200,50); u-b: UniformShare 200 -> key (200,200).
    // Equal ShareMax=200 — ShareMin (50 < 200) breaks the tie.
    Assert.Equal<string list>(
        [ "u-a"; "u-b" ],
        ProjectRows.orderedRows (ColumnSort(ByShare, Ascending)) false Set.empty tiedMax |> parentUrlsOf)

[<Fact>]
let ``ByStatus ascending orders the five status tiers`` () =
    let groups =
        ProjectRows.compute
            [| att "u-active" "P" "a" hostA false false 100.0 1.0 1.0 0
               att "u-nonew" "P" "a" hostA false true 100.0 1.0 1.0 0
               att "u-susp" "P" "a" hostA true false 100.0 1.0 1.0 0
               att "u-one" "P" "a" hostA true false 100.0 1.0 1.0 0
               att "u-one" "P" "b" hostB false false 100.0 1.0 1.0 0
               att "u-mixed" "P" "a" hostA true false 100.0 1.0 1.0 0
               att "u-mixed" "P" "b" hostB false true 100.0 1.0 1.0 0 |]
    Assert.Equal<string list>(
        [ "u-active"; "u-nonew"; "u-susp"; "u-one"; "u-mixed" ],
        ProjectRows.orderedRows (ColumnSort(ByStatus, Ascending)) false Set.empty groups |> parentUrlsOf)

// Direct compareRows-level pins for the RowLevel tiebreak — NOT routed through
// orderedRows/List.sortWith, which is documented stable and so would silently
// preserve groupSlots' parent-then-children construction order even if the
// Level tiebreak were dropped from compareRows (a false green caught during
// mutation falsification of this step: dropping the Level line left I2/I3
// green because stability alone reproduces the right on-screen order for
// THESE test sizes). The Level field is the load-bearing mechanism per the
// design comment on RowLevel; pin it independent of sort-implementation
// stability.
[<Fact>]
let ``compareRows orders ParentRow before any ChildRow of the same group, under every sort`` () =
    let g =
        ProjectRows.compute
            [| att "u" "P" "a" hostA false false 100.0 1.0 1.0 0
               att "u" "P" "b" hostB false false 100.0 1.0 1.0 0 |]
        |> Array.head
    let parent = ProjectRows.parentKey g
    let child = ProjectRows.childKey g g.Attachments.[0]
    for sort in allProjectSorts do
        Assert.True(ProjectRows.compareRows sort parent child < 0, sprintf "parent < child failed for %A" sort)
        Assert.True(ProjectRows.compareRows sort child parent > 0, sprintf "child > parent failed for %A" sort)

[<Fact>]
let ``compareRows orders children of the same group by (HostName, HostId), under every sort`` () =
    let g =
        ProjectRows.compute
            [| att "u" "P" "b" hostB false false 100.0 1.0 1.0 0
               att "u" "P" "a" hostA false false 100.0 1.0 1.0 0 |]
        |> Array.head
    let childHostA = g.Attachments |> Array.find (fun a -> a.HostName = "a")
    let childHostB = g.Attachments |> Array.find (fun a -> a.HostName = "b")
    let keyA = ProjectRows.childKey g childHostA
    let keyB = ProjectRows.childKey g childHostB
    for sort in allProjectSorts do
        Assert.True(ProjectRows.compareRows sort keyA keyB < 0, sprintf "host-a < host-b failed for %A" sort)

[<Fact>]
let ``expanding a single-attachment group in aggregate scope yields parent plus one child`` () =
    let groups = ProjectRows.compute [| att "u" "P" "a" hostA false false 100.0 1.0 1.0 0 |]
    let rows = ProjectRows.orderedRows DefaultSort true (Set.ofList [ "u" ]) groups
    match rows with
    | [ ParentSlot(g, true); ChildSlot(g2, a) ] ->
        Assert.Equal("u", g.MasterUrl)
        Assert.Equal("u", g2.MasterUrl)
        Assert.Equal(hostA, a.HostId)
    | other -> Assert.Fail(sprintf "expected [ParentSlot; ChildSlot], got %A" other)
