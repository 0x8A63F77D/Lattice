module Lattice.Aggregation.Tests.MessageLogTests

open System
open Xunit
open FsCheck
open FsCheck.Xunit
open Lattice.App.Aggregation

let hostA = Guid.NewGuid()
let hostB = Guid.NewGuid()

let entry hostId seqno ticks (body: string) =
    { Key = { HostId = hostId; Seqno = seqno; TimestampTicks = ticks }; Message = body }

[<Fact>]
let ``ingest returns new entries and retains them`` () =
    let log, added = MessageLog.ingest hostA [| entry hostA 1 10L "m1"; entry hostA 2 20L "m2" |] (MessageLog.empty 100)
    Assert.Equal(2, added.Length)
    Assert.Equal(2, (MessageLog.merged log).Length)

[<Fact>]
let ``reconnect replay is a no-op: same batch twice adds nothing`` () =
    let batch = [| entry hostA 1 10L "m1"; entry hostA 2 20L "m2" |]
    let log1, _ = MessageLog.ingest hostA batch (MessageLog.empty 100)
    let log2, added = MessageLog.ingest hostA batch log1
    Assert.Empty added
    Assert.Equal<MessageLog<string>>(log1, log2)

[<Fact>]
let ``daemon restart: reused seqno with different timestamp is a new line, history retained`` () =
    let log1, _ = MessageLog.ingest hostA [| entry hostA 1 10L "old-life" |] (MessageLog.empty 100)
    let log2, added = MessageLog.ingest hostA [| entry hostA 1 99L "new-life" |] log1
    Assert.Single added |> ignore
    Assert.Equal(2, (MessageLog.merged log2).Length)

[<Fact>]
let ``capacity keeps the newest per host`` () =
    let log1, _ = MessageLog.ingest hostA [| for i in 1 .. 5 -> entry hostA i (int64 (i * 10)) $"m{i}" |] (MessageLog.empty 3)
    let retained = MessageLog.merged log1
    Assert.Equal(3, retained.Length)
    Assert.All(retained, fun e -> Assert.True(e.Key.Seqno >= 3))

[<Fact>]
let ``merged stream is time-ordered across hosts`` () =
    let log1, _ = MessageLog.ingest hostA [| entry hostA 1 10L "a1"; entry hostA 2 30L "a2" |] (MessageLog.empty 100)
    let log2, _ = MessageLog.ingest hostB [| entry hostB 1 20L "b1" |] log1
    let bodies = MessageLog.merged log2 |> Array.map (fun e -> e.Message)
    Assert.Equal<string[]>([| "a1"; "b1"; "a2" |], bodies)

[<Fact>]
let ``prune drops removed hosts`` () =
    let log1, _ = MessageLog.ingest hostA [| entry hostA 1 10L "a" |] (MessageLog.empty 100)
    let log2, _ = MessageLog.ingest hostB [| entry hostB 1 20L "b" |] log1
    let pruned = MessageLog.prune (System.Collections.Generic.HashSet [ hostA ]) log2
    Assert.Single(MessageLog.merged pruned) |> ignore

let batchGen hostId =
    gen {
        let! n = Gen.choose (0, 12)
        let! entries =
            Gen.listOfLength n (gen {
                let! seqno = Gen.choose (1, 6)
                let! ticks = Gen.elements [ 10L; 20L; 30L ]
                return entry hostId seqno ticks $"s{seqno}t{ticks}"
            })
        return Array.ofList entries
    }

type LogArbs =
    static member Batches() =
        gen {
            let! b1 = batchGen hostA
            let! b2 = batchGen hostA
            return (b1, b2)
        }
        |> Arb.fromGen

[<Property>]
let ``ingest is idempotent`` () =
    let testFn (b1, b2) =
        let log1, _ = MessageLog.ingest hostA b1 (MessageLog.empty 50)
        let log2, _ = MessageLog.ingest hostA b2 log1
        let log3, added = MessageLog.ingest hostA b2 log2
        log3 = log2 && Array.isEmpty added
    Prop.forAll (Arb.fromGen (LogArbs.Batches().Generator)) testFn

[<Property>]
let ``retained set is the distinct union, capped to newest`` () =
    let testFn (b1, b2) =
        let log1, _ = MessageLog.ingest hostA b1 (MessageLog.empty 50)
        let log2, _ = MessageLog.ingest hostA b2 log1
        let expected =
            Array.append b1 b2
            |> Array.distinctBy (fun e -> e.Key)
            |> Array.sortBy (fun e -> e.Key.TimestampTicks, e.Key.Seqno)
        MessageLog.merged log2 = expected
    Prop.forAll (Arb.fromGen (LogArbs.Batches().Generator)) testFn

[<Property>]
let ``delta is exactly the not-yet-known entries`` () =
    let testFn (b1, b2) =
        let log1, _ = MessageLog.ingest hostA b1 (MessageLog.empty 50)
        let _, added = MessageLog.ingest hostA b2 log1
        let knownKeys = b1 |> Array.map (fun e -> e.Key) |> Set.ofArray
        let expected =
            b2 |> Array.distinctBy (fun e -> e.Key) |> Array.filter (fun e -> not (knownKeys.Contains e.Key))
        (added |> Array.map (fun e -> e.Key) |> Set.ofArray) = (expected |> Array.map (fun e -> e.Key) |> Set.ofArray)
    Prop.forAll (Arb.fromGen (LogArbs.Batches().Generator)) testFn
