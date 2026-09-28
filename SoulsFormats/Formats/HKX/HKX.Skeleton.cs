using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;

namespace SoulsFormats
{
    public partial class HKX
    {
        /// <summary>
        /// An animation skeleton (hkaSkeleton): bones in parent-first order with their reference
        /// pose. Animation tracks address bones by index into <see cref="Bones"/>.
        /// </summary>
        public class Skeleton
        {
            /// <summary>
            /// Skeleton name, e.g. "Master"; may be null.
            /// </summary>
            public string Name { get; set; }

            /// <summary>
            /// Bones by index.
            /// </summary>
            public List<Bone> Bones { get; set; }

            /// <summary>
            /// Names of the float slots that float tracks bind to.
            /// </summary>
            public List<string> FloatSlots { get; set; }
        }

        /// <summary>
        /// One skeleton bone and its reference (rest) pose relative to its parent.
        /// </summary>
        public class Bone
        {
            /// <summary>
            /// Bone name; may be null. Not unique in every file.
            /// </summary>
            public string Name { get; set; }

            /// <summary>
            /// Index of the parent bone, or -1 for a root.
            /// </summary>
            public short ParentIndex { get; set; }

            /// <summary>
            /// Whether animation leaves this bone's translation at the reference pose.
            /// </summary>
            public bool LockTranslation { get; set; }

            /// <summary>
            /// Reference-pose translation in the parent's space.
            /// </summary>
            public Vector3 Translation { get; set; }

            /// <summary>
            /// Reference-pose rotation in the parent's space.
            /// </summary>
            public Quaternion Rotation { get; set; }

            /// <summary>
            /// Reference-pose scale.
            /// </summary>
            public Vector3 Scale { get; set; }
        }

        /// <summary>
        /// Reads every hkaSkeleton in the file (a DeS skeleton.hkx holds one). Supports Havok 5.x
        /// with 4-byte pointers; the layout follows soulstruct-havok's Havok 5.5.0 types
        /// (Grimrukh, GPL-3.0-or-later).
        /// </summary>
        public List<Skeleton> ReadSkeletons()
        {
            if (PointerSize != 4 || !ContentsVersion.StartsWith("Havok-5."))
                throw new NotSupportedException($"Skeleton layouts are not known for {ContentsVersion} with {PointerSize}-byte pointers.");

            var skeletons = new List<Skeleton>();
            foreach (HKXObject obj in Objects)
            {
                if (obj.ClassName != "hkaSkeleton")
                    continue;
                int s = obj.Section, a = obj.Address;
                var skeleton = new Skeleton
                {
                    Name = ReadStringPtr(s, a),
                    Bones = new List<Bone>(),
                    FloatSlots = new List<string>(),
                };

                (int parents, int parentCount) = GetArray(s, a + 4);
                (int pose, int poseCount) = GetArray(s, a + 20);
                var bones = GetPointerArray(s, a + 12);
                if (parentCount != bones.Count || poseCount != bones.Count)
                    throw new InvalidDataException($"hkaSkeleton has {bones.Count} bones, {parentCount} parents and {poseCount} poses.");

                var parentReader = GetReader(s, Math.Max(parents, 0));
                var poseReader = GetReader(s, Math.Max(pose, 0));
                foreach (HKXObject bone in bones)
                {
                    // hkQsTransform: translation, rotation (x, y, z, w), scale, each 16 bytes.
                    Vector4 t = poseReader.ReadVector4(), r = poseReader.ReadVector4(), sc = poseReader.ReadVector4();
                    skeleton.Bones.Add(new Bone
                    {
                        Name = ReadStringPtr(bone.Section, bone.Address),
                        LockTranslation = GetReader(bone.Section, bone.Address + 4).ReadByte() != 0,
                        ParentIndex = parentReader.ReadInt16(),
                        Translation = ToVector3(t),
                        Rotation = new Quaternion(r.X, r.Y, r.Z, r.W),
                        Scale = ToVector3(sc),
                    });
                }

                (int slots, int slotCount) = GetArray(s, a + 28);
                for (int i = 0; i < slotCount; i++)
                    skeleton.FloatSlots.Add(ReadStringPtr(s, slots + i * PointerSize));
                skeletons.Add(skeleton);
            }
            return skeletons;
        }

        // hkStringPtr: a local pointer to a C string, or null.
        private string ReadStringPtr(int section, int address)
        {
            return Sections[section].LocalFixups.TryGetValue(address, out int target) ? ReadCString(section, target) : null;
        }
    }
}
