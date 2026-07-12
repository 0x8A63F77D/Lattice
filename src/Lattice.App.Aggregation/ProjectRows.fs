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

/// Row identity in the Projects grid: parent per MasterUrl, child per
/// (MasterUrl, host). DU structural equality is the reconciler key equality.
type ProjectRowKey =
    | ParentKey of masterUrl: string
    | ChildKey of masterUrl: string * hostId: Guid
