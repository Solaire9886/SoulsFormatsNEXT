using System;
using System.Collections.Generic;
using System.Numerics;

namespace SoulsFormats
{
    public partial class HKX
    {
        /// <summary>
        /// Geometric form of a <see cref="CollisionShape"/>.
        /// </summary>
        public enum CollisionShapeKind
        {
            /// <summary>
            /// Triangle mesh: <see cref="CollisionShape.Vertices"/> and <see cref="CollisionShape.Indices"/>.
            /// </summary>
            Mesh,

            /// <summary>
            /// Box centred on the origin: <see cref="CollisionShape.HalfExtents"/>.
            /// </summary>
            Box,

            /// <summary>
            /// Sphere centred on the origin: <see cref="CollisionShape.Radius"/>.
            /// </summary>
            Sphere,

            /// <summary>
            /// Capsule from <see cref="CollisionShape.PointA"/> to <see cref="CollisionShape.PointB"/>
            /// (the hemisphere centres) with <see cref="CollisionShape.Radius"/>.
            /// </summary>
            Capsule,

            /// <summary>
            /// Cylinder of <see cref="CollisionShape.Radius"/> whose end faces lie that convex
            /// radius beyond <see cref="CollisionShape.PointA"/> and <see cref="CollisionShape.PointB"/>
            /// (see <see cref="CollisionShape.ConvexRadius"/>).
            /// </summary>
            Cylinder,

            /// <summary>
            /// Convex hull of <see cref="CollisionShape.Vertices"/>.
            /// </summary>
            ConvexHull,
        }

        /// <summary>
        /// One collision shape of a rigid body, in the shape's own space; <see cref="Transform"/>
        /// places it in the file's space. No coordinate conversion. Havok's convex radius is
        /// included in box, sphere, capsule and cylinder sizes; hull vertices are as stored, without
        /// it.
        /// </summary>
        public class CollisionShape
        {
            /// <summary>
            /// Which fields describe the geometry.
            /// </summary>
            public CollisionShapeKind Kind { get; set; }

            /// <summary>
            /// Havok class of the shape, e.g. "hkpBoxShape".
            /// </summary>
            public string ShapeClass { get; set; }

            /// <summary>
            /// Shape space to file space: the rigid body's transform and any transform wrappers.
            /// Rigid; row-vector convention, as System.Numerics.
            /// </summary>
            public Matrix4x4 Transform { get; set; }

            /// <summary>
            /// Mesh vertices or hull points.
            /// </summary>
            public List<Vector3> Vertices { get; set; }

            /// <summary>
            /// Mesh triangle list, three vertex indices per triangle, wound clockwise seen from the
            /// front as the file's storage meshes are: (c - a) x (b - a) points out of the solid side.
            /// </summary>
            public List<int> Indices { get; set; }

            /// <summary>
            /// Box half extents.
            /// </summary>
            public Vector3 HalfExtents { get; set; }

            /// <summary>
            /// Sphere, capsule or cylinder radius.
            /// </summary>
            public float Radius { get; set; }

            /// <summary>
            /// Havok's convex radius, the rounding shell around the shape's core (+16 in every
            /// convex shape). Already included in the sizes above.
            /// </summary>
            public float ConvexRadius { get; set; }

            /// <summary>
            /// First capsule or cylinder axis point.
            /// </summary>
            public Vector3 PointA { get; set; }

            /// <summary>
            /// Second capsule or cylinder axis point.
            /// </summary>
            public Vector3 PointB { get; set; }

            /// <summary>
            /// The mesh subpart's first material word (FromSoftware's surface type); 0 when absent.
            /// </summary>
            public uint Material { get; set; }
        }

        /// <summary>
        /// Reads the collision shapes of every rigid body: storage meshes (the form of DeS map
        /// collision), simple meshes, boxes, spheres, capsules, cylinders and convex hulls, through
        /// MOPP, convex-translate and convex-transform wrappers. Supports Havok 5.x with 4-byte
        /// pointers. Storage-mesh offsets follow the Havok 5.5.0 layouts documented by
        /// soulstruct-havok (Grimrukh, GPL-3.0-or-later); the other shapes' offsets were read from
        /// the DeS files. Unsupported shapes are skipped and their classes added to
        /// <paramref name="skippedShapes"/> when given.
        /// </summary>
        public List<CollisionShape> ReadCollisionShapes(List<string> skippedShapes = null)
        {
            if (PointerSize != 4 || !ContentsVersion.StartsWith("Havok-5."))
                throw new NotSupportedException($"Collision layouts are not known for {ContentsVersion} with {PointerSize}-byte pointers.");

            var shapes = new List<CollisionShape>();
            foreach (HKXObject body in Objects)
            {
                if (body.ClassName != "hkpRigidBody")
                    continue;
                // hkpEntity.motion.motionState.transform; hkpWorldObject.collidable.shape.
                Matrix4x4 transform = ReadTransform(body.Section, body.Address + 224);
                HKXObject shape = GetPointedObject(body.Section, body.Address + 16);
                if (shape != null)
                    ReadShape(shape, transform, shapes, skippedShapes);
            }
            return shapes;
        }

        private void ReadShape(HKXObject shape, Matrix4x4 transform, List<CollisionShape> shapes, List<string> skippedShapes)
        {
            int s = shape.Section, a = shape.Address;
            switch (shape.ClassName)
            {
                case "hkpMoppBvTreeShape":
                    ReadChild(s, a + 52, transform, shapes, skippedShapes); // child.childShape
                    break;
                case "hkpConvexTranslateShape":
                    Vector4 t = GetReader(s, a + 32).ReadVector4();
                    ReadChild(s, a + 24, Matrix4x4.CreateTranslation(t.X, t.Y, t.Z) * transform, shapes, skippedShapes);
                    break;
                case "hkpConvexTransformShape":
                    ReadChild(s, a + 24, ReadTransform(s, a + 32) * transform, shapes, skippedShapes);
                    break;
                case "hkpStorageExtendedMeshShape":
                    foreach (HKXObject storage in GetPointerArray(s, a + 192))
                        shapes.Add(ReadStorageMesh(storage, shape.ClassName, transform, vector4Vertices: true, indicesPerTriangle: 4));
                    break;
                case "CustomParamStorageExtendedMeshShape":
                    // FromSoftware's subclass: materialArray (CustomMeshParameter pointers, one per
                    // subpart) holds the surface type as materialNameData.
                    List<HKXObject> storages = GetPointerArray(s, a + 192);
                    List<HKXObject> parameters = GetPointerArray(s, a + 216);
                    for (int i = 0; i < storages.Count; i++)
                    {
                        CollisionShape mesh = ReadStorageMesh(storages[i], shape.ClassName, transform, vector4Vertices: true, indicesPerTriangle: 4);
                        if (i < parameters.Count)
                            mesh.Material = GetReader(parameters[i].Section, parameters[i].Address + 32).ReadUInt32();
                        shapes.Add(mesh);
                    }
                    break;
                case "hkpStorageMeshShape":
                    foreach (HKXObject storage in GetPointerArray(s, a + 96))
                        shapes.Add(ReadStorageMesh(storage, shape.ClassName, transform, vector4Vertices: false, indicesPerTriangle: 3));
                    break;
                case "hkpSimpleMeshShape":
                    shapes.Add(ReadSimpleMesh(shape, transform));
                    break;
                case "hkpBoxShape":
                {
                    // Half extents at +32. A zero half extent is legal: m03_03 has a flat box.
                    CollisionShape box = NewConvexShape(CollisionShapeKind.Box, shape, transform);
                    box.HalfExtents = ToVector3(GetReader(s, a + 32).ReadVector4()) + new Vector3(box.ConvexRadius);
                    shapes.Add(box);
                    break;
                }
                case "hkpSphereShape":
                {
                    CollisionShape sphere = NewConvexShape(CollisionShapeKind.Sphere, shape, transform);
                    sphere.Radius = sphere.ConvexRadius;
                    shapes.Add(sphere);
                    break;
                }
                case "hkpCapsuleShape":
                {
                    // Hemisphere centres at +32 and +48.
                    var br = GetReader(s, a + 32);
                    CollisionShape capsule = NewConvexShape(CollisionShapeKind.Capsule, shape, transform);
                    capsule.PointA = ToVector3(br.ReadVector4());
                    capsule.PointB = ToVector3(br.ReadVector4());
                    capsule.Radius = capsule.ConvexRadius;
                    shapes.Add(capsule);
                    break;
                }
                case "hkpCylinderShape":
                {
                    // Core radius at +20 and end points at +32 and +48, inside the convex radius;
                    // the end points' w lanes hold the full radius.
                    var br = GetReader(s, a + 20);
                    CollisionShape cylinder = NewConvexShape(CollisionShapeKind.Cylinder, shape, transform);
                    cylinder.Radius = br.ReadSingle() + cylinder.ConvexRadius;
                    br.Position = a + 32;
                    cylinder.PointA = ToVector3(br.ReadVector4());
                    cylinder.PointB = ToVector3(br.ReadVector4());
                    shapes.Add(cylinder);
                    break;
                }
                case "hkpConvexVerticesShape":
                    shapes.Add(ReadConvexVertices(shape, transform));
                    break;
                default:
                    skippedShapes?.Add(shape.ClassName);
                    break;
            }
        }

        private void ReadChild(int section, int address, Matrix4x4 transform, List<CollisionShape> shapes, List<string> skippedShapes)
        {
            HKXObject child = GetPointedObject(section, address);
            if (child != null)
                ReadShape(child, transform, shapes, skippedShapes);
        }

        // hkTransform: three rotation columns, then the translation.
        private Matrix4x4 ReadTransform(int section, int address)
        {
            var br = GetReader(section, address);
            Vector4 c0 = br.ReadVector4(), c1 = br.ReadVector4(), c2 = br.ReadVector4(), t = br.ReadVector4();
            return new Matrix4x4(
                c0.X, c0.Y, c0.Z, 0,
                c1.X, c1.Y, c1.Z, 0,
                c2.X, c2.Y, c2.Z, 0,
                t.X, t.Y, t.Z, 1);
        }

        private static Vector3 ToVector3(Vector4 v) => new Vector3(v.X, v.Y, v.Z);

        private List<HKXObject> GetPointerArray(int section, int address)
        {
            var result = new List<HKXObject>();
            (int data, int count) = GetArray(section, address);
            for (int i = 0; i < count; i++)
            {
                HKXObject obj = GetPointedObject(section, data + i * PointerSize);
                if (obj != null)
                    result.Add(obj);
            }
            return result;
        }

        private static CollisionShape NewShape(CollisionShapeKind kind, string shapeClass, Matrix4x4 transform) => new CollisionShape
        {
            Kind = kind,
            ShapeClass = shapeClass,
            Transform = transform,
            Vertices = new List<Vector3>(),
            Indices = new List<int>(),
        };

        private CollisionShape NewConvexShape(CollisionShapeKind kind, HKXObject shape, Matrix4x4 transform)
        {
            CollisionShape result = NewShape(kind, shape.ClassName, transform);
            result.ConvexRadius = GetReader(shape.Section, shape.Address + 16).ReadSingle();
            return result;
        }

        // Extended storage keeps hkVector4 vertices and four indices per triangle; the older
        // storage mesh keeps float triples and three indices. The fourth word is uninitialised
        // exporter memory (mostly 0, else fill patterns such as 0xCDCD and 0xDDDD) and is not
        // read.
        private CollisionShape ReadStorageMesh(HKXObject storage, string shapeClass, Matrix4x4 transform, bool vector4Vertices, int indicesPerTriangle)
        {
            int s = storage.Section, a = storage.Address;
            CollisionShape mesh = NewShape(CollisionShapeKind.Mesh, shapeClass, transform);

            (int vertexData, int vertexCount) = GetArray(s, a + 8);
            var br = GetReader(s, Math.Max(vertexData, 0));
            if (vector4Vertices)
            {
                for (int i = 0; i < vertexCount; i++)
                    mesh.Vertices.Add(ToVector3(br.ReadVector4()));
            }
            else
            {
                for (int i = 0; i + 2 < vertexCount; i += 3)
                    mesh.Vertices.Add(br.ReadVector3());
            }

            (int index16Data, int index16Count) = GetArray(s, a + 20);
            (int index32Data, int index32Count) = GetArray(s, a + 32);
            bool wide = index16Count == 0;
            int indexCount = wide ? index32Count : index16Count;
            br = GetReader(s, Math.Max(wide ? index32Data : index16Data, 0));
            for (int i = 0; i + indicesPerTriangle <= indexCount; i += indicesPerTriangle)
            {
                for (int k = 0; k < indicesPerTriangle; k++)
                {
                    int index = wide ? br.ReadInt32() : br.ReadUInt16();
                    if (k < 3)
                        mesh.Indices.Add(index);
                }
            }

            (int materialData, int materialCount) = GetArray(s, a + 56);
            if (materialCount > 0)
                mesh.Material = GetReader(s, materialData).ReadUInt32();
            return mesh;
        }

        // hkVector4 vertices at +24; triangles at +36, each three int32 indices and a welding word
        // padded to 16 bytes.
        private CollisionShape ReadSimpleMesh(HKXObject shape, Matrix4x4 transform)
        {
            int s = shape.Section, a = shape.Address;
            CollisionShape mesh = NewShape(CollisionShapeKind.Mesh, shape.ClassName, transform);
            (int vertexData, int vertexCount) = GetArray(s, a + 24);
            var br = GetReader(s, Math.Max(vertexData, 0));
            for (int i = 0; i < vertexCount; i++)
                mesh.Vertices.Add(ToVector3(br.ReadVector4()));
            (int triangleData, int triangleCount) = GetArray(s, a + 36);
            for (int i = 0; i < triangleCount; i++)
            {
                br = GetReader(s, triangleData + i * 16);
                mesh.Indices.Add(br.ReadInt32());
                mesh.Indices.Add(br.ReadInt32());
                mesh.Indices.Add(br.ReadInt32());
            }
            return mesh;
        }

        // Vertices in blocks of four at +64 (x, y, z lanes of three hkVector4 each), their count at
        // +76. The face planes at +80 are not read: many hulls store a face as several coplanar
        // planes, and the hull is fully defined by its vertices.
        private CollisionShape ReadConvexVertices(HKXObject shape, Matrix4x4 transform)
        {
            int s = shape.Section, a = shape.Address;
            (int blockData, _) = GetArray(s, a + 64);
            int vertexCount = GetReader(s, a + 76).ReadInt32();
            CollisionShape hull = NewConvexShape(CollisionShapeKind.ConvexHull, shape, transform);
            for (int i = 0; i < vertexCount; i++)
            {
                var br = GetReader(s, blockData + i / 4 * 48 + i % 4 * 4);
                float x = br.ReadSingle();
                br.Position += 12;
                float y = br.ReadSingle();
                br.Position += 12;
                hull.Vertices.Add(new Vector3(x, y, br.ReadSingle()));
            }
            return hull;
        }
    }
}
