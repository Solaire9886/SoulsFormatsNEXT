using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;

namespace SoulsFormats
{
    public partial class HKX
    {
        /// <summary>
        /// A bone transform as Havok stores it (hkQsTransform): translation, rotation and scale,
        /// relative to the parent bone.
        /// </summary>
        public struct QsTransform
        {
            /// <summary>
            /// Translation in the parent's space.
            /// </summary>
            public Vector3 Translation;

            /// <summary>
            /// Rotation in the parent's space.
            /// </summary>
            public Quaternion Rotation;

            /// <summary>
            /// Scale.
            /// </summary>
            public Vector3 Scale;
        }

        /// <summary>
        /// One skeletal animation decoded to evenly spaced frames.
        /// </summary>
        public class Animation
        {
            /// <summary>
            /// Havok class of the source animation, e.g. "hkaWaveletSkeletalAnimation".
            /// </summary>
            public string ClassName { get; set; }

            /// <summary>
            /// Length in seconds. Frame i is at i * Duration / (frame count - 1).
            /// </summary>
            public float Duration { get; set; }

            /// <summary>
            /// Skeleton bone index of each transform track.
            /// </summary>
            public List<short> TrackToBone { get; set; }

            /// <summary>
            /// Frames[frame][track].
            /// </summary>
            public List<QsTransform[]> Frames { get; set; }

            /// <summary>
            /// Extracted root motion (hkaDefaultAnimatedReferenceFrame), evenly spaced over
            /// <see cref="Duration"/>: translation in X, Y, Z and the angle in radians about the up
            /// axis in W, both relative to the first sample. Null when the animation has none.
            /// </summary>
            public List<Vector4> ExtractedMotion { get; set; }
        }

        /// <summary>
        /// Reads every skeletal animation bound in the file, decoded to frames. Supports
        /// interleaved animations (Havok 5.x) and wavelet-compressed animations (Havok 5.1.0 and
        /// 5.5.0 with eight-frame blocks, the DeS forms), with 4-byte pointers. Unsupported animations are
        /// skipped and their classes added to <paramref name="skippedAnimations"/> when given.
        /// The wavelet decoder follows a behavioural specification of the game's sampler.
        /// </summary>
        public List<Animation> ReadAnimations(List<string> skippedAnimations = null)
        {
            if (PointerSize != 4 || !ContentsVersion.StartsWith("Havok-5."))
                throw new NotSupportedException($"Animation layouts are not known for {ContentsVersion} with {PointerSize}-byte pointers.");

            var animations = new List<Animation>();
            foreach (HKXObject binding in Objects)
            {
                if (binding.ClassName != "hkaAnimationBinding")
                    continue;
                // hkaAnimationBinding: animation pointer, transformTrackToBoneIndices.
                HKXObject source = GetPointedObject(binding.Section, binding.Address);
                if (source == null)
                    continue;
                int trackCount = GetReader(source.Section, source.Address + 16).ReadInt32();
                List<QsTransform[]> frames;
                if (source.ClassName == "hkaInterleavedSkeletalAnimation")
                    frames = ReadInterleavedFrames(source, trackCount);
                else if (source.ClassName == "hkaWaveletSkeletalAnimation" && ContentsVersion.StartsWith("Havok-5.5."))
                    frames = ReadWaveletFrames(source, trackCount, 0);
                else if (source.ClassName == "hkaWaveletSkeletalAnimation" && ContentsVersion.StartsWith("Havok-5.1."))
                    frames = ReadWaveletFrames(source, trackCount, -4);
                else
                    frames = null;
                if (frames == null)
                {
                    skippedAnimations?.Add(source.ClassName);
                    continue;
                }

                var trackToBone = new List<short>();
                (int indices, int indexCount) = GetArray(binding.Section, binding.Address + 4);
                var br = GetReader(binding.Section, Math.Max(indices, 0));
                for (int i = 0; i < trackCount; i++)
                    trackToBone.Add(i < indexCount ? br.ReadInt16() : (short)i);

                animations.Add(new Animation
                {
                    ClassName = source.ClassName,
                    Duration = GetReader(source.Section, source.Address + 12).ReadSingle(),
                    TrackToBone = trackToBone,
                    Frames = frames,
                    ExtractedMotion = ReadExtractedMotion(source),
                });
            }
            return animations;
        }

        // hkaAnimation.extractedMotion (Havok 5.1.0 has no numberOfFloatTracks before it) pointing
        // to an hkaDefaultAnimatedReferenceFrame: up, forward, duration, then the samples.
        private List<Vector4> ReadExtractedMotion(HKXObject animation)
        {
            int offset = ContentsVersion.StartsWith("Havok-5.1.") ? 20 : 24;
            HKXObject frame = GetPointedObject(animation.Section, animation.Address + offset);
            if (frame?.ClassName != "hkaDefaultAnimatedReferenceFrame")
                return null;
            (int samples, int count) = GetArray(frame.Section, frame.Address + 52);
            var br = GetReader(frame.Section, Math.Max(samples, 0));
            var motion = new List<Vector4>(count);
            for (int i = 0; i < count; i++)
                motion.Add(br.ReadVector4());
            return motion;
        }

        // hkaInterleavedSkeletalAnimation: frames of hkQsTransforms, track-major within a frame.
        private List<QsTransform[]> ReadInterleavedFrames(HKXObject animation, int trackCount)
        {
            var frames = new List<QsTransform[]>();
            (int data, int count) = GetArray(animation.Section, animation.Address + 36);
            if (trackCount <= 0)
                return frames;
            var br = GetReader(animation.Section, Math.Max(data, 0));
            for (int f = 0; f < count / trackCount; f++)
            {
                var frame = new QsTransform[trackCount];
                for (int t = 0; t < trackCount; t++)
                {
                    Vector4 translation = br.ReadVector4(), rotation = br.ReadVector4(), scale = br.ReadVector4();
                    frame[t] = new QsTransform
                    {
                        Translation = ToVector3(translation),
                        Rotation = new Quaternion(rotation.X, rotation.Y, rotation.Z, rotation.W),
                        Scale = ToVector3(scale),
                    };
                }
                frames.Add(frame);
            }
            return frames;
        }

        // The inverse transform of an eight-frame block: frame j = c[0] + sum over i >= 1 of
        // c[i] * InverseWavelet8[i - 1, j].
        private static readonly float[,] InverseWavelet8 =
        {
            { -0.5f, -0.25f, 0f, 0.25f, 0.5f, 0.5f, 0.5f, 0.5f },
            { -0.5f, 0.0625f, 0.625f, 0.1875f, -0.25f, -0.25f, -0.25f, -0.25f },
            { 0f, -0.0625f, -0.125f, -0.1875f, -0.25f, 0.25f, 0.75f, 0.75f },
            { -0.5f, 0.625f, -0.25f, -0.125f, 0f, 0f, 0f, 0f },
            { 0f, -0.125f, -0.25f, 0.75f, -0.25f, -0.125f, 0f, 0f },
            { 0f, 0f, 0f, -0.125f, -0.25f, 0.75f, -0.25f, -0.25f },
            { 0f, 0f, 0f, 0f, 0f, -0.125f, -0.25f, 0.75f },
        };

        private const int WaveletBlockSize = 8;

        // hkaWaveletSkeletalAnimation. All offsets are into dataBuffer, which DeS stores
        // little-endian inside the big-endian packfile. Each block holds eight frames of every
        // dynamic value, coded one value after another; static values and the per-track mask fill
        // in the rest of each transform. Havok 5.1.0 has no numberOfFloatTracks, so its later
        // fields sit 4 bytes earlier (shift -4).
        private List<QsTransform[]> ReadWaveletFrames(HKXObject animation, int trackCount, int shift)
        {
            var header = GetReader(animation.Section, animation.Address + 20);
            int floatTrackCount = shift == 0 ? header.ReadInt32() : 0;
            int a = animation.Address + shift;
            header.Position = a + 36;
            int poseCount = header.ReadInt32();
            int blockSize = header.ReadInt32();
            header.ReadByte(); // maxBitWidth: superseded by the per-value bit widths
            int preserved = header.ReadByte();
            header.Position = a + 48;
            int dynamicCount = header.ReadInt32();
            int offsetIdx = header.ReadInt32(), scaleIdx = header.ReadInt32(), bitWidthIdx = header.ReadInt32();
            int staticMaskIdx = header.ReadInt32(), staticValueIdx = header.ReadInt32();
            int blockIndexIdx = header.ReadInt32(), blockCount = header.ReadInt32();
            int quantizedIdx = header.ReadInt32();
            if (blockSize != WaveletBlockSize || preserved != 0)
                return null;

            (int bufferAddress, int bufferLength) = GetArray(animation.Section, a + 88);
            // Two bytes of padding: the value stream is read in 16-bit words.
            byte[] buffer = new byte[bufferLength + 2];
            Array.Copy(Sections[animation.Section].Data, bufferAddress, buffer, 0, bufferLength);
            var br = new BinaryReaderEx(false, buffer);

            float[] offsets = br.GetSingles(offsetIdx, dynamicCount);
            float[] scales = br.GetSingles(scaleIdx, dynamicCount);
            float[] staticValues = br.GetSingles(staticValueIdx, (offsetIdx - staticValueIdx) / 4);
            ushort[] masks = br.GetUInt16s(staticMaskIdx, trackCount + floatTrackCount);

            // values[frame][dynamic value]
            var values = new float[blockCount * WaveletBlockSize][];
            for (int f = 0; f < values.Length; f++)
                values[f] = new float[dynamicCount];
            var coefficients = new float[WaveletBlockSize];
            for (int b = 0; b < blockCount; b++)
            {
                int position = quantizedIdx + br.GetInt32(blockIndexIdx + b * 4);
                for (int d = 0; d < dynamicCount; d++)
                {
                    int bitWidth = buffer[bitWidthIdx + d];
                    position = DecodeWaveletCoefficients(buffer, position, bitWidth, scales[d], offsets[d], coefficients);
                    for (int j = 0; j < WaveletBlockSize; j++)
                    {
                        float value = coefficients[0];
                        for (int i = 1; i < WaveletBlockSize; i++)
                            value += coefficients[i] * InverseWavelet8[i - 1, j];
                        values[b * WaveletBlockSize + j][d] = value;
                    }
                }
            }

            var frames = new List<QsTransform[]>();
            for (int f = 0; f < Math.Min(poseCount, values.Length); f++)
                frames.Add(AssembleWaveletPose(masks, trackCount, staticValues, values[f]));
            return frames;
        }

        // One dynamic value's eight coefficients, dequantised. A zero mask (one bit per
        // coefficient, set = the quantised zero) precedes the stored values, which are bitWidth
        // bits each, least significant first, in little-endian 16-bit words. Returns the position
        // after the value's data.
        private static int DecodeWaveletCoefficients(byte[] buffer, int position, int bitWidth, float scale, float offset, float[] coefficients)
        {
            int zero = scale != 0 ? (int)(-offset * (1 << bitWidth) / scale) : 0;
            if (zero == 1 << bitWidth)
                zero--;
            float step = scale / (1 << bitWidth);

            byte zeroMask = buffer[position];
            int stream = position + 1;
            uint accumulator = 0;
            int available = 0, stored = 0;
            for (int i = 0; i < WaveletBlockSize; i++)
            {
                int quantized;
                if ((zeroMask & (1 << i)) != 0)
                {
                    quantized = zero;
                }
                else
                {
                    while (available < bitWidth)
                    {
                        accumulator |= (uint)(buffer[stream] | buffer[stream + 1] << 8) << available;
                        stream += 2;
                        available += 16;
                    }
                    quantized = (int)(accumulator & ((1u << bitWidth) - 1));
                    accumulator >>= bitWidth;
                    available -= bitWidth;
                    stored++;
                }
                coefficients[i] = step * (quantized + 0.5f) + offset;
            }
            return position + (WaveletBlockSize + stored * bitWidth + 7) / 8;
        }

        // Per-track mask: bits 0-1, 2-3, 4-5 give the translation, rotation and scale type (2 =
        // identity, nothing stored); otherwise each component is dynamic when its bit is set
        // (translation x/y/z 8/7/6, rotation x/y/z/w 12/11/10/9, scale x/y/z 15/14/13) and the
        // next static value when clear. A stored rotation w of +-2 means w is rebuilt from x, y, z
        // with the stored sign.
        private static QsTransform[] AssembleWaveletPose(ushort[] masks, int trackCount, float[] staticValues, float[] dynamicValues)
        {
            var pose = new QsTransform[trackCount];
            int s = 0, d = 0;
            float Take(int mask, int bit) => (mask & (1 << bit)) != 0 ? dynamicValues[d++] : staticValues[s++];
            for (int t = 0; t < trackCount; t++)
            {
                int m = masks[t];
                var transform = new QsTransform { Rotation = Quaternion.Identity, Scale = Vector3.One };
                if ((m & 3) != 2)
                    transform.Translation = new Vector3(Take(m, 8), Take(m, 7), Take(m, 6));
                if ((m >> 2 & 3) != 2)
                {
                    float x = Take(m, 12), y = Take(m, 11), z = Take(m, 10), w = Take(m, 9);
                    if (Math.Abs(w) == 2f)
                    {
                        float sum = x * x + y * y + z * z;
                        float rebuilt = sum <= 1f ? MathF.Sqrt(1f - sum) : 0f;
                        w = w <= 0f ? -rebuilt : rebuilt;
                    }
                    transform.Rotation = new Quaternion(x, y, z, w);
                }
                if ((m >> 4 & 3) != 2)
                    transform.Scale = new Vector3(Take(m, 15), Take(m, 14), Take(m, 13));
                pose[t] = transform;
            }
            return pose;
        }
    }
}
