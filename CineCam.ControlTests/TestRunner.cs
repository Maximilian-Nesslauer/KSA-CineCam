namespace CineCam.ControlTests;

internal static class TestRunner
{
    public static int Main()
    {
        int failures = 0;
        ControlTests.Run((name, test) =>
        {
            try
            {
                test();
                Console.WriteLine("PASS " + name);
            }
            catch (Exception ex)
            {
                failures++;
                Console.WriteLine($"FAIL {name}: {ex}");
            }
        }, (condition, message) =>
        {
            if (!condition) throw new InvalidOperationException(message);
        });
        Console.WriteLine(failures == 0 ? "All control tests passed." : $"{failures} control test(s) failed.");
        return failures == 0 ? 0 : 1;
    }
}
