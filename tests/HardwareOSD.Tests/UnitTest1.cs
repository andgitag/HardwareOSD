// Several suites exercise real timers and a background polling thread. Running them in parallel
// makes otherwise healthy timing assertions flaky under a loaded CI/build machine, so the assembly
// is intentionally serialized; individual tests still exercise concurrency internally.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace HardwareOSD.Tests;

public class UnitTest1
{
    [Fact]
    public void Test1()
    {

    }
}
