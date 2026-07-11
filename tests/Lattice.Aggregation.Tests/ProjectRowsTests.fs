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
        // Key uniqueness precondition: one attachment per (url, host).
        return atts |> List.distinctBy (fun a -> a.MasterUrl, a.HostId) |> Array.ofList
    }

type ProjectArbs =
    static member Attachments() = Arb.fromGen attachmentsGen

[<Property(Arbitrary = [| typeof<ProjectArbs> |])>]
let ``attachment conservation: groups partition the input`` (atts: ProjectAttachment[]) =
    let groups = ProjectRows.compute atts
    let regrouped = groups |> Array.collect (fun g -> g.Attachments) |> Array.sortBy (fun a -> a.MasterUrl, a.HostId)
    regrouped = (atts |> Array.sortBy (fun a -> a.MasterUrl, a.HostId))

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
