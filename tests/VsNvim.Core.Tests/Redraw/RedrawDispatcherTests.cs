using System.Collections.Generic;
using VsNvim.Core.Redraw;
using Xunit;

namespace VsNvim.Core.Tests.Redraw;

public class RedrawDispatcherTests
{
    [Fact]
    public void Process_RaisesModeCursorAndFlushInOrder()
    {
        var dispatcher = new RedrawDispatcher();
        var log = new List<string>();
        dispatcher.ModeChanged += e => log.Add($"mode:{e.ModeName}:{e.ModeIndex}");
        dispatcher.CursorGoto += e => log.Add($"cur:{e.Grid}:{e.Row}:{e.Column}");
        dispatcher.FlushReceived += () => log.Add("flush");

        // Mirrors a real redraw payload: array of batches.
        object[] redrawArgs =
        {
            new object[] { "grid_cursor_goto", new object[] { 1L, 3L, 5L } },
            new object[] { "mode_change", new object[] { "insert", 2L } },
            new object[] { "win_viewport", new object[] { 1L, 0L } }, // ignored
            new object[] { "flush" },
        };

        dispatcher.Process(redrawArgs);

        Assert.Equal(new[] { "cur:1:3:5", "mode:insert:2", "flush" }, log);
    }
}
