using System.Collections.Generic;
using System.Xml;
using System.Xml.Serialization;

namespace SoulsFormats
{
    public partial class FFXDLSE
    {
#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member
        public class FXEffect : FXSerializable
        {
            internal override string ClassName => "FXSerializableEffect";

            internal override int Version => 5;

            [XmlAttribute]
            public int ID { get; set; }

            public ParamList ParamList1 { get; set; }

            public ParamList ParamList2 { get; set; }

            public StateMap StateMap { get; set; }

            public ResourceSet ResourceSet { get; set; }

            // Demon's Souls only - 22 effects in the mounted corpus use this envelope instead of the
            // ordinary one above: an effect that's really 3 (so far always identical) child effects
            // behind opaque handles, no separate tail of its own (docs/context.md part 58). Which
            // child the game actually uses/how is not established - preserved losslessly, not
            // interpreted. AggregateChildren is empty for an ordinary effect.
            public List<FXEffect> AggregateChildren { get; set; }
            public List<uint> AggregateHandles { get; set; }

            public FXEffect()
            {
                ParamList1 = new ParamList();
                ParamList2 = new ParamList();
                StateMap = new StateMap();
                ResourceSet = new ResourceSet();
                AggregateChildren = new List<FXEffect>();
                AggregateHandles = new List<uint>();
            }

            internal FXEffect(BinaryReaderEx br, List<string> classNames) : base(br, classNames) { }

            protected internal override void Deserialize(BinaryReaderEx br, List<string> classNames)
            {
                int mode = DemonsSouls ? br.ReadInt32() : 0;
                if (!DemonsSouls)
                    br.AssertInt32(0);

                if (mode == 1)
                {
                    ID = br.ReadInt32();
                    br.AssertInt32(0);
                    br.AssertInt16(2);
                    int childCount = br.ReadInt32();
                    AggregateChildren = new List<FXEffect>(childCount);
                    AggregateHandles = new List<uint>(childCount);
                    for (int i = 0; i < childCount; i++)
                    {
                        br.AssertInt16(1);
                        AggregateHandles.Add(br.ReadUInt32());
                        AggregateChildren.Add(new FXEffect(br, classNames));
                    }
                    return;
                }

                ID = br.ReadInt32();
                br.AssertInt32(0);
                br.AssertInt32(0);
                br.AssertInt32(2); // Param list count?
                br.AssertInt16(0);
                br.AssertInt16(2); // Judging by the order of class names, this must be an always-empty DLVector
                br.AssertInt32(0);

                ParamList1 = new ParamList(br, classNames);
                ParamList2 = new ParamList(br, classNames);

                StateMap = new StateMap(br, classNames);
                ResourceSet = new ResourceSet(br, classNames);
                // Demon's Souls: version 3 has no trailing byte, version 4 has one (always 0).
                // DS2's version 5 always has it.
                if (!DemonsSouls || ReadVersion >= 4)
                    br.AssertByte(0);
            }

            internal override void AddClassNames(List<string> classNames)
            {
                base.AddClassNames(classNames);
                DLVector.AddClassNames(classNames);

                if (AggregateChildren.Count > 0)
                {
                    foreach (FXEffect child in AggregateChildren)
                        child.AddClassNames(classNames);
                    return;
                }

                ParamList1.AddClassNames(classNames);
                ParamList2.AddClassNames(classNames);

                StateMap.AddClassNames(classNames);
                ResourceSet.AddClassNames(classNames);
            }

            protected internal override void Serialize(BinaryWriterEx bw, List<string> classNames)
            {
                if (AggregateChildren.Count > 0)
                {
                    bw.WriteInt32(1);
                    bw.WriteInt32(ID);
                    bw.WriteInt32(0);
                    bw.WriteInt16(2);
                    bw.WriteInt32(AggregateChildren.Count);
                    for (int i = 0; i < AggregateChildren.Count; i++)
                    {
                        bw.WriteInt16(1);
                        bw.WriteUInt32(AggregateHandles[i]);
                        AggregateChildren[i].Write(bw, classNames);
                    }
                    return;
                }

                bw.WriteInt32(0);
                bw.WriteInt32(ID);
                bw.WriteInt32(0);
                bw.WriteInt32(0);
                bw.WriteInt32(2);
                bw.WriteInt16(0);
                bw.WriteInt16(2);
                bw.WriteInt32(0);

                ParamList1.Write(bw, classNames);
                ParamList2.Write(bw, classNames);

                StateMap.Write(bw, classNames);
                ResourceSet.Write(bw, classNames);
                bw.WriteByte(0);
            }
        }
#pragma warning restore CS1591 // Missing XML comment for publicly visible type or member
    }
}
