namespace Lattice.App.Aggregation

open System

/// One host's attachment of one project. The App layer projects
/// (ProjectSnapshot, host identity) into this — no GuiRpc types here.
type ProjectAttachment =
    { MasterUrl: string
      ProjectName: string
      HostId: Guid
      HostName: string
      TaskCount: int
      ResourceShare: float
      /// HostExpavgCredit (RAC) — per-host, never account-level User* (design §Data model).
      AvgCredit: float
      /// HostTotalCredit — per-host, never account-level User*.
      TotalCredit: float
      IsSuspended: bool
      NoNewTasks: bool }

/// Per-host status, derived (suspended wins when both flags are set).
type AttachmentStatus =
    | Active
    | Suspended
    | NoNewTasks

/// Parent-row status aggregate, the three design tiers (design 2a):
/// all same / one deviating kind / mixed deviations.
type StatusSummary =
    | AllSame of AttachmentStatus
    | OneDeviation of status: AttachmentStatus * deviants: int * total: int
    | MixedStatus of suspended: int * noNewTasks: int

type ShareSummary =
    | UniformShare of float
    | VariesShare of min: float * max: float

/// One project aggregated across its in-scope host attachments.
type ProjectGroup =
    { MasterUrl: string
      DisplayName: string
      /// Sorted by host name (stable child order).
      Attachments: ProjectAttachment[]
      Share: ShareSummary
      Status: StatusSummary
      AvgCredit: float
      TotalCredit: float
      TaskCount: int }

/// Which parent-aggregate column the Projects grid is sorted by. Only the parent
/// AGGREGATE sorts; child (per-host) rows always follow their parent, so this
/// orders ProjectGroups, never individual attachments (design: aggregate sort).
type ProjectSortColumn =
    | ByName
    | ByHostCount
    | ByShare
    | ByAvgCredit
    | ByTotalCredit
    | ByStatus

type SortDirection =
    | Ascending
    | Descending

/// The grid's active sort. DefaultSort reproduces compute's RAC-descending
/// hierarchy order (and lights no header arrow); ColumnSort is a header choice.
type ProjectSort =
    | DefaultSort
    | ColumnSort of column: ProjectSortColumn * direction: SortDirection

/// Comparable aggregate facts of one group — precomputed once so comparers
/// never parse display strings.
type GroupSortKey =
    { NameKey: string
      HostCount: int
      ShareMax: float
      ShareMin: float
      AvgCredit: float
      TotalCredit: float
      StatusRank: int
      MasterUrl: string }

/// Position inside a group. Structural comparison over this DU IS the
/// intra-group order: ParentRow < ChildRow (case order), children by
/// (HostName, HostId) — compute's attachment order, direction-INVARIANT.
type RowLevel =
    | ParentRow
    | ChildRow of hostName: string * hostId: Guid

type RowSortKey = { Group: GroupSortKey; Level: RowLevel }

/// One display row, in canonical order.
type RowSlot =
    | ParentSlot of group: ProjectGroup * isExpanded: bool
    | ChildSlot of group: ProjectGroup * attachment: ProjectAttachment

module ProjectRows =
    let status (a: ProjectAttachment) : AttachmentStatus =
        if a.IsSuspended then Suspended
        elif a.NoNewTasks then NoNewTasks
        else Active

    /// Summarize per-host statuses into the three design tiers. Operates on the
    /// (suspended, noNew) counts — active is the remainder. NOTE: nothing in
    /// this module gets compile-time totality over AttachmentStatus (`status`
    /// only CONSTRUCTS cases; the counting here is value-equality — both are
    /// invisible to the exhaustiveness checker). A new case must be threaded
    /// through by hand; the compile-time tripwire is the exhaustive
    /// OneDeviation match in the status-consistency FsCheck property
    /// (ProjectRowsTests.fs), which fails under --warnaserror. The tuple-match
    /// below is total over int × int with no DU wildcard.
    let internal summarize (statuses: AttachmentStatus[]) : StatusSummary =
        let count s = statuses |> Array.filter (fun x -> x = s) |> Array.length
        let suspended = count Suspended
        let noNew = count NoNewTasks
        let total = statuses.Length
        match suspended, noNew with
        | 0, 0 -> AllSame Active
        | s, 0 when s = total -> AllSame Suspended
        | 0, n when n = total -> AllSame NoNewTasks
        | s, 0 -> OneDeviation(Suspended, s, total)
        | 0, n -> OneDeviation(NoNewTasks, n, total)
        | s, n -> MixedStatus(s, n)

    let internal shareSummary (shares: float[]) : ShareSummary =
        let lo = Array.min shares
        let hi = Array.max shares
        if lo = hi then UniformShare lo else VariesShare(lo, hi)

    /// Groups in-scope attachments by MasterUrl (design 2a). Attachments sort
    /// by host name; groups sort by aggregate RAC descending (default sort).
    /// Precondition: caller passes in-scope attachments only (ViewSlice's
    /// AllRows), so single-host scope degrades naturally (no Varies, no
    /// children to show).
    /// Guarantee: duplicate (MasterUrl, host) attachments collapse to the
    /// first occurrence — SnapshotBuilder does not dedup project entries and
    /// BOINC replies are parsed leniently, so a malformed get_state can carry
    /// the same master_url twice; child-row key uniqueness (Reconcile.diff's
    /// precondition) is established here, where the keys are constructed.
    let compute (attachments: ProjectAttachment[]) : ProjectGroup[] =
        attachments
        |> Array.distinctBy (fun a -> a.MasterUrl, a.HostId)
        |> Array.groupBy (fun a -> a.MasterUrl)
        |> Array.map (fun (url, atts) ->
            let sorted = atts |> Array.sortBy (fun a -> a.HostName, a.HostId)
            { MasterUrl = url
              DisplayName =
                sorted
                |> Array.tryPick (fun a -> if a.ProjectName = "" then None else Some a.ProjectName)
                |> Option.defaultValue url
              Attachments = sorted
              Share = shareSummary (sorted |> Array.map (fun a -> a.ResourceShare))
              Status = summarize (sorted |> Array.map status)
              AvgCredit = sorted |> Array.sumBy (fun a -> a.AvgCredit)
              TotalCredit = sorted |> Array.sumBy (fun a -> a.TotalCredit)
              TaskCount = sorted |> Array.sumBy (fun a -> a.TaskCount) })
        |> Array.sortByDescending (fun g -> g.AvgCredit)

    /// Severity rank for status sorting (healthy → degraded); ascending puts the
    /// all-active groups first. Total over StatusSummary — including the inner
    /// AttachmentStatus of AllSame — with no DU wildcard, so a new status case is
    /// a compile error here.
    let private statusRank (s: StatusSummary) : int =
        match s with
        | AllSame Active -> 0
        | AllSame NoNewTasks -> 1
        | AllSame Suspended -> 2
        | OneDeviation _ -> 3
        | MixedStatus _ -> 4

    /// (max, min) of a group's per-host resource shares — ByShare's primary and
    /// tiebreak. UniformShare collapses to (v, v); VariesShare is already stored
    /// as (min, max), so the pair swaps on the way out.
    let private shareBounds (share: ShareSummary) : float * float =
        match share with
        | UniformShare v -> v, v
        | VariesShare(min, max) -> max, min

    /// Reorder the parent groups by a chosen aggregate column (children follow
    /// their parent, so only groups are ordered here). Stable ascending sort,
    /// reversed for descending. `compute` keeps its RAC-descending default; this
    /// is applied only when the user has picked a column via a header click.
    /// TODO(#57): superseded by orderedRows/compareRows; deleted in a later step.
    let sortGroups (column: ProjectSortColumn) (descending: bool) (groups: ProjectGroup[]) : ProjectGroup[] =
        let ascending =
            match column with
            | ByName -> groups |> Array.sortBy (fun g -> g.DisplayName.ToLowerInvariant())
            | ByHostCount -> groups |> Array.sortBy (fun g -> g.Attachments.Length)
            | ByShare -> groups |> Array.sortBy (fun g -> shareBounds g.Share)
            | ByAvgCredit -> groups |> Array.sortBy (fun g -> g.AvgCredit)
            | ByTotalCredit -> groups |> Array.sortBy (fun g -> g.TotalCredit)
            | ByStatus -> groups |> Array.sortBy (fun g -> statusRank g.Status)
        if descending then Array.rev ascending else ascending

    /// The comparable value one column extracts from a group — a DU so comparing
    /// two values of the same column is structural and total.
    type private ColumnKey =
        | TextKey of string
        | CountKey of int
        | ShareKey of max: float * min: float
        | CreditKey of float
        | RankKey of int

    let private columnKey (column: ProjectSortColumn) (g: GroupSortKey) : ColumnKey =
        match column with
        | ByName -> TextKey g.NameKey
        | ByHostCount -> CountKey g.HostCount
        | ByShare -> ShareKey(g.ShareMax, g.ShareMin)
        | ByAvgCredit -> CreditKey g.AvgCredit
        | ByTotalCredit -> CreditKey g.TotalCredit
        | ByStatus -> RankKey g.StatusRank

    /// Direction = swapped operands, never negation.
    let private orient (direction: SortDirection) (a: 'k) (b: 'k) : int =
        match direction with
        | Ascending -> compare a b
        | Descending -> compare b a

    let private groupOrder (sort: ProjectSort) (a: GroupSortKey) (b: GroupSortKey) : int =
        match sort with
        | DefaultSort -> orient Descending a.AvgCredit b.AvgCredit
        | ColumnSort(column, direction) -> orient direction (columnKey column a) (columnKey column b)

    /// THE total display order. Direction touches only the group component; the
    /// MasterUrl tie and the intra-group RowLevel order are direction-invariant.
    let compareRows (sort: ProjectSort) (a: RowSortKey) (b: RowSortKey) : int =
        [ groupOrder sort a.Group b.Group
          compare a.Group.MasterUrl b.Group.MasterUrl
          compare a.Level b.Level ]
        |> List.tryFind (fun c -> c <> 0)
        |> Option.defaultValue 0

    /// Precomputed comparable facts of one group (NameKey lowercased; share
    /// bounds via shareBounds; StatusRank via statusRank; HostCount is the
    /// in-scope attachment count).
    let groupKey (g: ProjectGroup) : GroupSortKey =
        let shareMax, shareMin = shareBounds g.Share
        { NameKey = g.DisplayName.ToLowerInvariant()
          HostCount = g.Attachments.Length
          ShareMax = shareMax
          ShareMin = shareMin
          AvgCredit = g.AvgCredit
          TotalCredit = g.TotalCredit
          StatusRank = statusRank g.Status
          MasterUrl = g.MasterUrl }

    let parentKey (g: ProjectGroup) : RowSortKey = { Group = groupKey g; Level = ParentRow }

    let childKey (g: ProjectGroup) (a: ProjectAttachment) : RowSortKey =
        { Group = groupKey g
          Level = ChildRow(a.HostName, a.HostId) }

    let slotKey (slot: RowSlot) : RowSortKey =
        match slot with
        | ParentSlot(g, _) -> parentKey g
        | ChildSlot(g, a) -> childKey g a

    /// Header-click toggle: same column flips direction; a new column selects it
    /// Ascending; from DefaultSort a click selects the column Ascending. There is
    /// no path back to DefaultSort. Exhaustive over ProjectSort × SortDirection —
    /// the guarded same-column branches need their unguarded companions spelled
    /// out (no DU wildcard) for the match to stay total.
    let toggleSort (column: ProjectSortColumn) (current: ProjectSort) : ProjectSort =
        match current with
        | DefaultSort -> ColumnSort(column, Ascending)
        | ColumnSort(c, Ascending) when c = column -> ColumnSort(column, Descending)
        | ColumnSort(c, Descending) when c = column -> ColumnSort(column, Ascending)
        | ColumnSort(_, Ascending) -> ColumnSort(column, Ascending)
        | ColumnSort(_, Descending) -> ColumnSort(column, Ascending)

    /// The one total display order. aggregate = multi-host presentation:
    /// children exist only there; collapsed groups contribute only their parent.
    let orderedRows
        (sort: ProjectSort)
        (aggregate: bool)
        (expanded: Set<string>)
        (groups: ProjectGroup[])
        : RowSlot list =
        let groupSlots (g: ProjectGroup) : RowSlot list =
            let isExpanded = aggregate && expanded.Contains g.MasterUrl
            let children =
                if isExpanded then
                    g.Attachments |> Array.map (fun a -> ChildSlot(g, a)) |> Array.toList
                else
                    []
            ParentSlot(g, isExpanded) :: children

        groups
        |> Array.toList
        |> List.collect groupSlots
        |> List.sortWith (fun a b -> compareRows sort (slotKey a) (slotKey b))

/// Row identity in the Projects grid: parent per MasterUrl, child per
/// (MasterUrl, host). DU structural equality is the reconciler key equality.
type ProjectRowKey =
    | ParentKey of masterUrl: string
    | ChildKey of masterUrl: string * hostId: Guid
