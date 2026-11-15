using System;
using System.IO;
using QDNH.Settings;
using Xunit;
namespace QDNH.Tests;
public class ConsoleEofTests
{
    [Fact]
    public void ClosedStdinRequestsCleanExitInsteadOfBusyLoop()
    {
        var original = Console.In;
        try { Console.SetIn(new StringReader("")); Assert.Equal("Q", Vars.In()); }
        finally { Console.SetIn(original); }
    }
}
