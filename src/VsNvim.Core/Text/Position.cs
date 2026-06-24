namespace VsNvim.Core.Text
{
    /// <summary>VS editor position: 0-based line, 0-based UTF-16 char column.</summary>
    public readonly struct VsPosition
    {
        public VsPosition(int line, int column)
        {
            Line = line;
            Column = column;
        }
        public int Line { get; }
        public int Column { get; }
    }

    /// <summary>Neovim position: 1-based row, 0-based UTF-8 byte column.</summary>
    public readonly struct NvimPosition
    {
        public NvimPosition(long row, long column)
        {
            Row = row;
            Column = column;
        }
        public long Row { get; }
        public long Column { get; }
    }
}
