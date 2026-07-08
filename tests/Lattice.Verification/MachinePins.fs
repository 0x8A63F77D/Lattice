/// Direct pins on HostMachine routing decisions. These are documentation-grade
/// spot checks; exhaustive coverage is the wrapper exploration in Properties.fs.
module Lattice.Verification.MachinePins

open Xunit
open Lattice.Core
open Lattice.Core.HostMachine

let private tick = { maxSeqno = Some 3; hasUnknownWorkunit = false }

[<Theory>]
[<InlineData(1, 1.0)>]
[<InlineData(2, 2.0)>]
[<InlineData(4, 8.0)>]
[<InlineData(6, 32.0)>]
[<InlineData(7, 60.0)>]
[<InlineData(99, 60.0)>]
let ``backoff schedule matches HostMonitor.BackoffDelay`` (attempt: int) (seconds: float) =
    Assert.Equal(System.TimeSpan.FromSeconds seconds, backoffDelay attempt)

[<Fact>]
let ``post-Connected failure resets the attempt counter to 1 (I4)`` () =
    let s = { initial with phase = TearingDown(OFailed("boom", true)); attempt = 7 }
    let s', cmds = step s EffectOk
    Assert.Equal(1, s'.attempt)
    Assert.True(cmds |> List.exists (function
        | PublishStatus(HostConnectionState.Retrying, 1, Some _, Some "boom", false) -> true
        | _ -> false))

[<Fact>]
let ``pre-Connected failure counts up`` () =
    let s = { initial with phase = TearingDown(OFailed("boom", false)); attempt = 2 }
    let s', _ = step s EffectOk
    Assert.Equal(3, s'.attempt)

[<Fact>]
let ``accept guard bars Connected when a config change is pending (I1)`` () =
    let s = { initial with phase = AcceptGuard }
    let s', cmds = step s (GuardObserved true)
    Assert.True(match s'.phase with TearingDown OConfigChanged -> true | _ -> false)
    Assert.False(cmds |> List.exists (function
        | PublishStatus(HostConnectionState.Connected, _, _, _, _) -> true
        | _ -> false))

[<Fact>]
let ``second consecutive mid-poll unauthorized escalates to the Failed path`` () =
    let s = { initial with phase = TickAwait; reachedConnected = true
                           reauthedSinceLastSuccess = true; hasPassword = true }
    let s', _ = step s (Faulted(Unauthorized "expired"))
    Assert.True(match s'.phase with TearingDown(OFailed("expired", true)) -> true | _ -> false)

[<Fact>]
let ``first mid-poll unauthorized triggers one silent re-auth`` () =
    let s = { initial with phase = TickAwait; reachedConnected = true; hasPassword = true }
    let s', cmds = step s (Faulted(Unauthorized "expired"))
    Assert.Equal(Reauthorizing, s'.phase)
    Assert.Equal<Command list>([ Authorize ], cmds)

[<Fact>]
let ``mid-poll unauthorized without a password parks`` () =
    let s = { initial with phase = TickAwait; reachedConnected = true; hasPassword = false }
    let s', _ = step s (Faulted(Unauthorized "expired"))
    Assert.True(match s'.phase with TearingDown(OAuthFailed "The host refused the password.") -> true | _ -> false)

[<Fact>]
let ``first tick replaces the log, second appends`` () =
    let s = { initial with phase = MsgGuard; tick = Some tick; firstTick = true }
    let s', cmds = step s (GuardObserved false)
    Assert.True(cmds |> List.exists (function PublishMessages true -> true | _ -> false))
    // The tick-completion triple (HostMonitor.cs:544-546) has NOT run yet at MsgGuard:
    // a re-auth retry after a refetch fault replays with the old cursor and the same
    // replaceLog decision.
    Assert.True(s'.firstTick)
    Assert.Equal(0, s'.lastSeqno)
    let sDone, _ = step { s' with phase = PostBuildGuard } (GuardObserved false)
    Assert.False(sDone.firstTick)
    Assert.Equal(3, sDone.lastSeqno)
    Assert.False(sDone.reauthedSinceLastSuccess)
    let s2 = { sDone with phase = MsgGuard; tick = Some { tick with maxSeqno = None } }
    let _, cmds2 = step s2 (GuardObserved false)
    Assert.True(cmds2 |> List.exists (function PublishMessages false -> true | _ -> false))

[<Fact>]
let ``completed tick resets the silent re-auth allowance`` () =
    let s = { initial with phase = PostBuildGuard; reachedConnected = true; hasPassword = true
                           reauthedSinceLastSuccess = true
                           tick = Some { maxSeqno = Some 5; hasUnknownWorkunit = false } }
    let s', _ = step s (GuardObserved false)
    Assert.False(s'.reauthedSinceLastSuccess)
    Assert.Equal(5, s'.lastSeqno)

[<Fact>]
let ``refetch unauthorized takes the silent re-auth path`` () =
    let s = { initial with phase = Refetching; reachedConnected = true; hasPassword = true }
    let s', cmds = step s (Faulted(Unauthorized "expired"))
    Assert.Equal(Reauthorizing, s'.phase)
    Assert.Equal<Command list>([ Authorize ], cmds)

[<Fact>]
let ``refetch unauthorized after a re-auth escalates`` () =
    // Pins the no-reset-at-TickFetched subtlety: the allowance flag must survive the
    // successful TickFetched that preceded the refetch, so this SECOND consecutive
    // unauthorized escalates to the Failed path instead of re-authing forever.
    let s = { initial with phase = Refetching; reachedConnected = true; hasPassword = true
                           reauthedSinceLastSuccess = true }
    let s', _ = step s (Faulted(Unauthorized "expired"))
    Assert.True(match s'.phase with TearingDown(OFailed("expired", true)) -> true | _ -> false)
