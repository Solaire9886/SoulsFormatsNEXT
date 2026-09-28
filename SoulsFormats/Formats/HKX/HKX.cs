using System;
using System.Collections.Generic;

namespace SoulsFormats
{
    /// <summary>
    /// A Havok packfile (binary .hkx) as used by DeS: sections of fixed-layout objects linked by
    /// fixup tables. Read-only. The container is version-independent; object layouts are not, so
    /// the typed readers (e.g. <see cref="ReadCollisionShapes"/>) state which versions they support.
    /// DeS files carry no type descriptions (their __types__ section is empty).
    /// </summary>
    public partial class HKX : SoulsFile<HKX>
    {
        /// <summary>
        /// Havok SDK version string, e.g. "Havok-5.5.0-r1".
        /// </summary>
        public string ContentsVersion { get; set; }

        /// <summary>
        /// Packfile format version from the header (5 for DeS).
        /// </summary>
        public int FileVersion { get; set; }

        /// <summary>
        /// Byte order of all data; true on PS3.
        /// </summary>
        public bool BigEndian { get; set; }

        /// <summary>
        /// Size of a pointer in the object layouts (4 on PS3).
        /// </summary>
        public byte PointerSize { get; set; }

        /// <summary>
        /// Sections in file order (__classnames__, __types__, __data__; the order varies by version).
        /// </summary>
        public List<Section> Sections { get; set; }

        /// <summary>
        /// Every object named by a virtual fixup, in table order.
        /// </summary>
        public List<HKXObject> Objects { get; set; }

        /// <summary>
        /// The top-level object (usually an hkRootLevelContainer), or null.
        /// </summary>
        public HKXObject Contents { get; set; }

        private Dictionary<(int, int), HKXObject> objectsByAddress;

        /// <summary>
        /// One packfile section. Addresses are offsets into <see cref="Data"/>.
        /// </summary>
        public class Section
        {
            /// <summary>
            /// Section tag, e.g. "__data__".
            /// </summary>
            public string Name { get; set; }

            /// <summary>
            /// Section contents up to the fixup tables.
            /// </summary>
            public byte[] Data { get; set; }

            /// <summary>
            /// Pointer address to target address within this section.
            /// </summary>
            public Dictionary<int, int> LocalFixups { get; set; }

            /// <summary>
            /// Pointer address to (section index, target address).
            /// </summary>
            public Dictionary<int, (int Section, int Address)> GlobalFixups { get; set; }
        }

        /// <summary>
        /// An object instance: its class name and where its fixed layout starts.
        /// </summary>
        public class HKXObject
        {
            /// <summary>
            /// Havok class name, e.g. "hkpRigidBody".
            /// </summary>
            public string ClassName { get; set; }

            /// <summary>
            /// Index of the section holding the object.
            /// </summary>
            public int Section { get; set; }

            /// <summary>
            /// Address of the object within its section.
            /// </summary>
            public int Address { get; set; }
        }

        /// <summary>
        /// Checks the two magic words, which read the same in either byte order.
        /// </summary>
        protected override bool Is(BinaryReaderEx br)
        {
            if (br.Length < 0x40)
                return false;
            return br.GetUInt32(0) == 0x57E0E057 && br.GetUInt32(4) == 0x10C0C010;
        }

        /// <summary>
        /// Reads the header, the sections and their fixup tables.
        /// </summary>
        protected override void Read(BinaryReaderEx br)
        {
            br.BigEndian = false;
            br.AssertUInt32(0x57E0E057);
            br.AssertUInt32(0x10C0C010);
            PointerSize = br.GetByte(0x10);
            BigEndian = br.GetByte(0x11) == 0;
            br.BigEndian = BigEndian;

            br.ReadInt32(); // user tag
            FileVersion = br.ReadInt32();
            if (FileVersion > 8)
                throw new NotSupportedException($"HKX packfile version {FileVersion} is not supported.");
            br.Skip(4); // layout rules
            int sectionCount = br.ReadInt32();
            int contentsSection = br.ReadInt32();
            int contentsAddress = br.ReadInt32();
            br.ReadInt32(); // contents class name section
            br.ReadInt32(); // contents class name address
            ContentsVersion = br.ReadFixStr(16);
            br.Skip(8); // flags, padding

            var headers = new List<(string Name, int[] Offsets)>();
            for (int i = 0; i < sectionCount; i++)
            {
                string name = br.ReadFixStr(20);
                headers.Add((name, br.ReadInt32s(7)));
            }

            // Offsets are relative to the section start: local, global and virtual fixups, then
            // exports, imports and end.
            Sections = new List<Section>();
            var virtualFixups = new List<(int Section, int Address, int NameSection, int NameAddress)>();
            for (int i = 0; i < sectionCount; i++)
            {
                (string name, int[] o) = headers[i];
                int start = o[0];
                var section = new Section
                {
                    Name = name,
                    Data = br.GetBytes(start, o[1]),
                    LocalFixups = new Dictionary<int, int>(),
                    GlobalFixups = new Dictionary<int, (int, int)>(),
                };
                // Tables are padded with -1 entries.
                for (int p = o[1]; p + 8 <= o[2]; p += 8)
                {
                    int src = br.GetInt32(start + p);
                    if (src != -1)
                        section.LocalFixups[src] = br.GetInt32(start + p + 4);
                }
                for (int p = o[2]; p + 12 <= o[3]; p += 12)
                {
                    int src = br.GetInt32(start + p);
                    if (src != -1)
                        section.GlobalFixups[src] = (br.GetInt32(start + p + 4), br.GetInt32(start + p + 8));
                }
                for (int p = o[3]; p + 12 <= o[4]; p += 12)
                {
                    int src = br.GetInt32(start + p);
                    if (src != -1)
                        virtualFixups.Add((i, src, br.GetInt32(start + p + 4), br.GetInt32(start + p + 8)));
                }
                Sections.Add(section);
            }

            Objects = new List<HKXObject>();
            objectsByAddress = new Dictionary<(int, int), HKXObject>();
            foreach (var v in virtualFixups)
            {
                var obj = new HKXObject { ClassName = ReadCString(v.NameSection, v.NameAddress), Section = v.Section, Address = v.Address };
                Objects.Add(obj);
                objectsByAddress[(v.Section, v.Address)] = obj;
            }
            objectsByAddress.TryGetValue((contentsSection, contentsAddress), out HKXObject contents);
            Contents = contents;
        }

        /// <summary>
        /// A reader over one section's data, positioned at an address.
        /// </summary>
        public BinaryReaderEx GetReader(int section, int address)
        {
            var br = new BinaryReaderEx(BigEndian, Sections[section].Data);
            br.Position = address;
            return br;
        }

        /// <summary>
        /// The object a pointer at this address refers to, or null for a null pointer.
        /// </summary>
        public HKXObject GetPointedObject(int section, int address)
        {
            if (!Sections[section].GlobalFixups.TryGetValue(address, out var target))
                return null;
            objectsByAddress.TryGetValue((target.Section, target.Address), out HKXObject obj);
            return obj;
        }

        /// <summary>
        /// Resolves an hkArray at this address (pointer, size, capacity and flags) to the address
        /// of its elements in the same section and their count. An empty array gives (-1, 0).
        /// </summary>
        public (int Address, int Count) GetArray(int section, int address)
        {
            var s = Sections[section];
            int count = new BinaryReaderEx(BigEndian, s.Data).GetInt32(address + PointerSize);
            if (count == 0 || !s.LocalFixups.TryGetValue(address, out int data))
                return (-1, 0);
            return (data, count);
        }

        private string ReadCString(int section, int address)
        {
            byte[] data = Sections[section].Data;
            int end = Array.IndexOf(data, (byte)0, address);
            return System.Text.Encoding.ASCII.GetString(data, address, end - address);
        }
    }
}
