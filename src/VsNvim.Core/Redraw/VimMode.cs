namespace VsNvim.Core.Redraw
{
    public enum VimMode
    {
        Unknown,
        Normal,
        OperatorPending,
        Insert,
        Replace,
        Visual,
        VisualLine,
        VisualBlock,
        CommandLine,
        Terminal
    }

    /// <summary>Maps the authoritative mode code from nvim_get_mode().mode to a VimMode.</summary>
    public static class VimModeMap
    {
        public static VimMode FromModeCode(string modeCode)
        {
            if (string.IsNullOrEmpty(modeCode))
                return VimMode.Unknown;

            // Operator-pending modes are "no", "nov", "noV", "no<C-v>".
            if (modeCode.StartsWith("no"))
                return VimMode.OperatorPending;

            switch (modeCode[0])
            {
                case 'n': return VimMode.Normal;
                case 'i': return VimMode.Insert;
                case 'R': return VimMode.Replace;
                case 'v': return VimMode.Visual;
                case 'V': return VimMode.VisualLine;
                case (char)0x16: return VimMode.VisualBlock; // CTRL-V
                case 'c': return VimMode.CommandLine;
                case 't': return VimMode.Terminal;
                default: return VimMode.Unknown;
            }
        }
    }
}
