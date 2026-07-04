using System.Xml.Linq;
using Lattice.Boinc.GuiRpc;
using Xunit;

namespace Lattice.Tests;

public class CcStatusTests
{
    private static XElement Reply(string name) => XElement.Parse(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", name)));

    [Fact]
    public void Parses_modes_and_suspend_reasons()
    {
        var status = CcStatus.Parse(Reply("get_cc_status.xml").Element("cc_status")!);

        Assert.Equal(RunMode.Auto, status.TaskMode);
        Assert.Equal(RunMode.Always, status.GpuMode);
        Assert.Equal(RunMode.Never, status.NetworkMode);
        Assert.Equal(SuspendReason.UserRequest, status.TaskSuspendReason);
        Assert.Equal(SuspendReason.NotSuspended, status.GpuSuspendReason);
        Assert.Equal(SuspendReason.NotSuspended, status.NetworkSuspendReason);
    }

    [Fact]
    public void Unknown_enum_integer_is_preserved()
    {
        var e = XElement.Parse("<cc_status><task_mode>99</task_mode></cc_status>");
        var status = CcStatus.Parse(e);
        Assert.Equal(99, (int)status.TaskMode);
    }

    [Fact]
    public void Missing_fields_default_to_zero_values()
    {
        var status = CcStatus.Parse(XElement.Parse("<cc_status/>"));
        Assert.Equal((RunMode)0, status.TaskMode);
        Assert.Equal(SuspendReason.NotSuspended, status.TaskSuspendReason);
    }
}
