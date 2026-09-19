using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Serialization;

namespace SoulsFormats
{
    /// <summary>
    /// An SFX configuration format used in DeS and DS2. DeS support is partial - see the
    /// DemonsSouls flag below and Soulbrandt's docs/ARCHITECTURE.md/context.md part 57 for status.
    /// Extension: .ffx
    /// </summary>
    public partial class FFXDLSE : SoulsFile<FFXDLSE>
    {
#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member
        public FXEffect Effect { get; set; }

        public FFXDLSE()
        {
            Effect = new FXEffect();
        }

        // Demon's Souls ships a real but distinct earlier revision of this same format under an
        // all-caps "DLSE" magic (DS2's is mixed-case "DLsE") - a shorter file header (4 bytes of
        // version info instead of 17) and no per-object length prefix in FXSerializable's own
        // header (see the DemonsSouls flag there). Confirmed empirically against a real DeS .ffx:
        // the class-name string table and every class's field layout checked so far are otherwise
        // identical to the DS2 shape this file already reads.
        internal static bool DemonsSouls;

        // Diagnostic-only, off by default: prints each object's class/version/position as it's
        // read, and hex-dumps an unimplemented Param/Evaluatable type's raw bytes instead of just
        // throwing. Left in place (not stripped after the investigation) because DeS Param types
        // 31/32 are still genuinely unresolved - see the doc-comment reference above.
        public static bool Trace;
        internal static int TraceDepth;

        protected override bool Is(BinaryReaderEx br)
        {
            if (br.Length < 4)
                return false;

            string magic = br.GetASCII(0, 4);
            return magic == "DLsE" || magic == "DLSE";
        }

        protected override void Read(BinaryReaderEx br)
        {
            br.BigEndian = false;
            string magic = br.GetASCII(br.Position, 4);
            DemonsSouls = magic == "DLSE";
            br.AssertASCII(magic);

            short classNameCount;
            if (DemonsSouls)
            {
                // 4-byte version pair (major/minor?) in place of DS2's longer fixed sequence below -
                // read, not asserted, until confirmed constant across more than one captured file.
                br.ReadInt16();
                br.ReadInt16();
                classNameCount = br.ReadInt16();
            }
            else
            {
                br.AssertByte(1);
                br.AssertByte(3);
                br.AssertByte(0);
                br.AssertByte(0);
                br.AssertInt32(0);
                br.AssertInt32(0);
                br.AssertByte(0);
                br.AssertInt32(1);
                classNameCount = br.ReadInt16();
            }

            var classNames = new List<string>(classNameCount);
            for (int i = 0; i < classNameCount; i++)
            {
                int length = br.ReadInt32();
                classNames.Add(br.ReadASCII(length));
            }

            if (Trace)
                Console.WriteLine("classNames: " + string.Join(", ", classNames.Select((n, i) => $"[{i}]{n}")));
            Effect = new FXEffect(br, classNames);
        }

        protected override void Write(BinaryWriterEx bw)
        {
            var classNames = new List<string>();
            Effect.AddClassNames(classNames);

            bw.BigEndian = false;
            bw.WriteASCII("DLsE");
            bw.WriteByte(1);
            bw.WriteByte(3);
            bw.WriteByte(0);
            bw.WriteByte(0);
            bw.WriteInt32(0);
            bw.WriteInt32(0);
            bw.WriteByte(0);
            bw.WriteInt32(1);
            bw.WriteInt16((short)classNames.Count);

            foreach (string className in classNames)
            {
                bw.WriteInt32(className.Length);
                bw.WriteASCII(className);
            }

            Effect.Write(bw, classNames);
        }

        #region XML Serialization
        private static XmlSerializer _ffxSerializer;
        private static XmlSerializer _stateSerializer;
        private static XmlSerializer _paramSerializer;

        private static XmlSerializer MakeSerializers(int returnIndex)
        {
            XmlSerializer[] serializers = XmlSerializer.FromTypes(
                new Type[] { typeof(FFXDLSE), typeof(State), typeof(Param) });

            _ffxSerializer = serializers[0];
            _stateSerializer = serializers[1];
            _paramSerializer = serializers[2];
            return serializers[returnIndex];
        }

        private static XmlSerializer FFXSerializer => _ffxSerializer ?? MakeSerializers(0);
        private static XmlSerializer StateSerializer => _stateSerializer ?? MakeSerializers(1);
        private static XmlSerializer ParamSerializer => _paramSerializer ?? MakeSerializers(2);

        public static FFXDLSE XmlDeserialize(Stream stream)
            => (FFXDLSE)FFXSerializer.Deserialize(stream);

        public static FFXDLSE XmlDeserialize(TextReader textReader)
            => (FFXDLSE)FFXSerializer.Deserialize(textReader);

        public static FFXDLSE XmlDeserialize(XmlReader xmlReader)
            => (FFXDLSE)FFXSerializer.Deserialize(xmlReader);

        public void XmlSerialize(Stream stream)
            => FFXSerializer.Serialize(stream, this);

        public void XmlSerialize(TextWriter textWriter)
            => FFXSerializer.Serialize(textWriter, this);

        public void XmlSerialize(XmlWriter xmlWriter)
            => FFXSerializer.Serialize(xmlWriter, this);
        #endregion

        private static class DLVector
        {
            public static List<int> Read(BinaryReaderEx br, List<string> classNames)
            {
                br.AssertInt16((short)(classNames.IndexOf("DLVector") + 1));
                int count = br.ReadInt32();
                return new List<int>(br.ReadInt32s(count));
            }

            public static void AddClassNames(List<string> classNames)
            {
                if (!classNames.Contains("DLVector"))
                    classNames.Add("DLVector");
            }

            public static void Write(BinaryWriterEx bw, List<string> classNames, List<int> vector)
            {
                bw.WriteInt16((short)(classNames.IndexOf("DLVector") + 1));
                bw.WriteInt32(vector.Count);
                bw.WriteInt32s(vector);
            }
        }

        public abstract class FXSerializable
        {
            internal abstract string ClassName { get; }

            internal abstract int Version { get; }

            // The version actually read from disk in DemonsSouls mode (Version above is DS2's
            // hardcoded expectation, not asserted there). FXEffect uses this - a real, observed
            // envelope difference, not a guess: version 3 has no trailing byte, version 4 has one
            // (always 0) - see docs/context.md part 58.
            internal int ReadVersion;

            internal FXSerializable() { }

            internal FXSerializable(BinaryReaderEx br, List<string> classNames)
            {
                long start = br.Position;
                br.AssertInt16((short)(classNames.IndexOf(ClassName) + 1));
                if (DemonsSouls)
                {
                    // No length prefix in this earlier revision, and per-class version numbers run
                    // behind DS2's (e.g. FXEffect is 4 here, 5 there) - read for visibility, not
                    // asserted, since the full per-class mapping isn't confirmed yet.
                    int ver = br.ReadInt32();
                    ReadVersion = ver;
                    if (Trace) Console.WriteLine($"{new string(' ', TraceDepth*2)}{ClassName} @0x{start:X} ver={ver}");
                    TraceDepth++;
                    Deserialize(br, classNames);
                    TraceDepth--;
                }
                else
                {
                    br.AssertInt32(Version);
                    int length = br.ReadInt32();
                    Deserialize(br, classNames);
                    if (br.Position != start + length)
                        throw new InvalidDataException("Failed to read all object data (or read too much of it).");
                }
            }

            protected internal abstract void Deserialize(BinaryReaderEx br, List<string> classNames);

            internal virtual void AddClassNames(List<string> classNames)
            {
                if (!classNames.Contains(ClassName))
                    classNames.Add(ClassName);
            }

            internal void Write(BinaryWriterEx bw, List<string> classNames)
            {
                long start = bw.Position;
                bw.WriteInt16((short)(classNames.IndexOf(ClassName) + 1));
                bw.WriteInt32(Version);
                bw.ReserveInt32($"{start:X}Length");
                Serialize(bw, classNames);
                bw.FillInt32($"{start:X}Length", (int)(bw.Position - start));
            }

            protected internal abstract void Serialize(BinaryWriterEx bw, List<string> classNames);
        }
#pragma warning restore CS1591 // Missing XML comment for publicly visible type or member
    }
}
