using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using LineType = System.UInt32;

namespace NodeVsDebugger
{
    /// <summary>
    /// Maps segments of a generated file to segments of source files.
    /// </summary>
    public class SourceMap
    {
        struct Segment
        {
            public Segment(LineType genLine, LineType genCol, LineType origLine, LineType origCol)
            {
                this.GeneratedLine = genLine;
                this.GeneratedColumn = genCol;
                this.OriginalLine = origLine;
                this.OriginalColumn = origCol;
            }

            public readonly LineType GeneratedLine;
            public readonly LineType GeneratedColumn;
            public readonly LineType OriginalLine;
            public readonly LineType OriginalColumn;

            class COrigLineColumnComparer : IComparer<Segment>
            {
                public int Compare(Segment x, Segment y)
                {
                    int result = x.OriginalLine.CompareTo(y.OriginalLine);
                    if (result == 0)
                        return x.OriginalColumn.CompareTo(y.OriginalColumn);
                    return result;
                }
            }

            public static readonly IComparer<Segment> OrigLineColumnComparer = new COrigLineColumnComparer();
        }

        struct GSegment
        {
            public GSegment(string sourceFile, LineType genCol, LineType origLine, LineType origCol)
            {
                this.SourceFile = sourceFile;
                this.GeneratedColumn = genCol;
                this.OriginalLine = origLine;
                this.OriginalColumn = origCol;
            }

            public readonly string SourceFile;
            public readonly LineType GeneratedColumn;
            public readonly LineType OriginalLine;
            public readonly LineType OriginalColumn;

            class CGeneratedColumnComparer : IComparer<GSegment>
            {
                public int Compare(GSegment x, GSegment y)
                {
                    return x.GeneratedColumn.CompareTo(y.GeneratedColumn);
                }
            }

            public static readonly IComparer<GSegment> GeneratedColumnComparer = new CGeneratedColumnComparer();
        };

        public static SourceMap ReadSourceMaps(string generatedFile, string[] sourceFiles, string data)
        {
            var list = new List<Segment>[sourceFiles.Length];
            var lines = new List<List<GSegment>>();
            int dataLen = data.Length;
            LineType currentLine = 0;
            int ptr = 0;

            int generatedCol = 0;
            int fileLine = 0;
            int fileCol = 0;

            // Read lines
            while (ptr < dataLen)
            {
                // Start line
                var gSegments = new List<GSegment>();
                lines.Add(gSegments);

                // Read segments
                while (ptr < dataLen && data[ptr] != ';')
                {
                    generatedCol += ReadInt(data, ref ptr);
                    int fileIndex = ReadInt(data, ref ptr);
                    fileLine += ReadInt(data, ref ptr);
                    fileCol += ReadInt(data, ref ptr);

                    var segments = list[fileIndex];
                    if (segments == null)
                        list[fileIndex] = segments = new List<Segment>();

                    segments.Add(new Segment(currentLine, checked((uint)generatedCol),
                                             checked((uint)fileLine), checked((uint)fileCol)));
                    gSegments.Add(new GSegment(sourceFiles[fileIndex], checked((uint)generatedCol),
                                               checked((uint)fileLine), checked((uint)fileCol)));

                    // Skip the rest of base64 chars
                    while (ptr < dataLen && IsBase64Char(data[ptr]))
                        ++ptr;

                    if (!(ptr < dataLen) || data[ptr] == ';')
                        break;

                    if (data[ptr] == ',')
                        ++ptr;
                    else
                        throw new FormatException();
                }

                // End line
                ++currentLine;
                ++ptr; // skip ';'
            }

            for (int i = 0; i < list.Length; i++)
                list[i].Sort(Segment.OrigLineColumnComparer);

            for (int i = 0; i < lines.Count; i++)
                lines[i].Sort(GSegment.GeneratedColumnComparer);

            var map = new SourceMap();
            map.GeneratedFile = generatedFile;
            map.SourceFiles = sourceFiles;
            map.m_sourceSegments = list.Select(_ => _.ToArray()).ToArray();
            map.m_generatedLines = lines.Select(_ => _.ToArray()).ToArray();
            return map;
        }

        const int s_signBit = 0;
        const uint s_signMask = 1 << s_signBit;
        const int s_contBit = 5;
        const uint s_contMask = 1 << s_contBit;
        static int ReadInt(string str, ref int ptr)
        {
            int value = 0;
            int bits;
            int shift = 0;
            do
            {
                bits = ReadBase64Char(str[ptr]);
                value |= (int)(bits & ~s_contMask) << shift;

                shift += 5;
                ++ptr;
            }
            while ((bits & s_contMask) == s_contMask);

            bool sign = (value & s_signMask) == s_signMask;

            value >>= (s_signBit + 1);

            if (sign)
                value = -value;

            return value;
        }

        static int ReadBase64Char(char c)
        {
            if (c >= 'A' && c <= 'Z')
                return c - 'A';

            if (c >= 'a' && c <= 'z')
                return c - 'a' + 26;

            if (c >= '0' && c <= '9')
                return c - '0' + 26 + 26;

            if (c == '+')
                return 26 + 26 + 10;

            if (c == '/')
                return 26 + 26 + 10 + 1;

            throw new FormatException();
        }

        static bool IsBase64Char(char c)
        {
            return
                c >= 'A' && c <= 'Z' ||
                c >= 'a' && c <= 'z' ||
                c >= '0' && c <= '9' ||
                c == '+' || c == '/';
        }

        SourceMap()
        {
        }

        public string GeneratedFile { get; private set; }

        public string[] SourceFiles { get; private set; }

        // m_generatedLines[lineIndex], sorted by GeneratedColumn
        GSegment[][] m_generatedLines;
        // m_sourceSegments[fileIndex], sorted by (SourceLine, SourceColumn)
        Segment[][] m_sourceSegments;

        public void TranslateGeneratedToSource(out string filename, ref LineType line, ref LineType column)
        {
            if (line >= m_generatedLines.Length)
            {
                filename = this.GeneratedFile;
                return;
            }

            var segments = m_generatedLines[line];
            int index = Array.BinarySearch(segments, new GSegment(null, column, 0, 0), GSegment.GeneratedColumnComparer);
            if (index >= 0)
            {
                var segment = segments[index];
                filename = segment.SourceFile;
                line = segment.OriginalLine;
                column = segment.OriginalColumn;
            }
            else
            {
                index = ~index;

                if (index < segments.Length)
                {
                    if (index > 0)
                        --index;

                    var segment = segments[index];
                    filename = segment.SourceFile;
                    line = segment.OriginalLine;
                    column = segment.OriginalColumn;
                }
                else
                    filename = this.GeneratedFile;
            }
        }

        public void TranslateSourceToGenerated(ref string filename, ref LineType line, ref LineType column)
        {
            int idx = Array.IndexOf(this.SourceFiles, filename);
            if (idx < 0)
                return;

            var segments = m_sourceSegments[idx];
            int index = Array.BinarySearch(segments, new Segment(0, 0, line, column), Segment.OrigLineColumnComparer);
            if (index >= 0)
            {
                var segment = segments[index];
                filename = this.GeneratedFile;
                line = segment.GeneratedLine;
                column = segment.GeneratedColumn;
            }
            else
            {
                index = ~index;

                if (index < segments.Length)
                {
                    if (index > 0)
                        --index;

                    var segment = segments[index];
                    filename = this.GeneratedFile;
                    line = segment.GeneratedLine;
                    column = segment.GeneratedColumn;
                }
            }
        }
    }
}
