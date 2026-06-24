namespace VsNvim.Core.Redraw
{
    public sealed class ModeChangeEvent
    {
        public ModeChangeEvent(string modeName, long modeIndex)
        {
            ModeName = modeName;
            ModeIndex = modeIndex;
        }
        public string ModeName { get; }
        public long ModeIndex { get; }
    }

    public sealed class CursorGotoEvent
    {
        public CursorGotoEvent(long grid, long row, long column)
        {
            Grid = grid;
            Row = row;
            Column = column;
        }
        public long Grid { get; }
        public long Row { get; }
        public long Column { get; }
    }
}
