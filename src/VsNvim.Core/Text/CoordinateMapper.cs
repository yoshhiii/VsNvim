using System;

namespace VsNvim.Core.Text
{
    /// <summary>Converts between nvim byte columns / 1-based rows and VS UTF-16 columns / 0-based lines.</summary>
    public static class CoordinateMapper
    {
        /// <summary>UTF-8 byte length of a Unicode code point.</summary>
        private static int Utf8Length(int codePoint)
        {
            if (codePoint <= 0x7F) return 1;
            if (codePoint <= 0x7FF) return 2;
            if (codePoint <= 0xFFFF) return 3;
            return 4;
        }

        /// <summary>0-based UTF-16 char column -> 0-based UTF-8 byte column. Clamps to end of line.</summary>
        public static int CharToByteColumn(string lineText, int charColumn)
        {
            if (lineText == null) throw new ArgumentNullException(nameof(lineText));
            int chars = Math.Min(charColumn, lineText.Length);
            int bytes = 0;
            int i = 0;
            while (i < chars)
            {
                char c = lineText[i];
                if (char.IsHighSurrogate(c) && i + 1 < lineText.Length && char.IsLowSurrogate(lineText[i + 1]))
                {
                    bytes += Utf8Length(char.ConvertToUtf32(c, lineText[i + 1]));
                    i += 2;
                }
                else
                {
                    bytes += Utf8Length(c);
                    i += 1;
                }
            }
            return bytes;
        }

        /// <summary>0-based UTF-8 byte column -> 0-based UTF-16 char column. Clamps to end of line.</summary>
        public static int ByteToCharColumn(string lineText, int byteColumn)
        {
            if (lineText == null) throw new ArgumentNullException(nameof(lineText));
            int bytes = 0;
            int i = 0;
            while (i < lineText.Length && bytes < byteColumn)
            {
                char c = lineText[i];
                if (char.IsHighSurrogate(c) && i + 1 < lineText.Length && char.IsLowSurrogate(lineText[i + 1]))
                {
                    bytes += Utf8Length(char.ConvertToUtf32(c, lineText[i + 1]));
                    i += 2;
                }
                else
                {
                    bytes += Utf8Length(c);
                    i += 1;
                }
            }
            return i;
        }

        public static NvimPosition ToNvim(VsPosition pos, string lineText) =>
            new NvimPosition(pos.Line + 1, CharToByteColumn(lineText, pos.Column));

        public static VsPosition ToVs(NvimPosition pos, string lineText) =>
            new VsPosition((int)(pos.Row - 1), ByteToCharColumn(lineText, (int)pos.Column));
    }
}
