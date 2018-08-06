﻿#define SCALE_TO_PMX_SIZE
//#undef SCALE_TO_PMX_SIZE
#define TRANSLATE_BONE_NAMES_TO_MMD
//#undef TRANSLATE_BONE_NAMES_TO_MMD
#define APPEND_IK_BONES
//#undef APPEND_IK_BONES
#define FIX_MMD_CENTER_BONES
//#undef FIX_MMD_CENTER_BONES
#define HIDE_UNITY_GENERATED_BONES
//#undef HIDE_UNITY_GENERATED_BONES

#define SKELETON_FORMAT_MMD
//#define SKELETON_FORMAT_MLTD

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using JetBrains.Annotations;
using MillionDance.Entities.Internal;
using MillionDance.Entities.Pmx;
using MillionDance.Entities.Pmx.Extensions;
using MillionDance.Extensions;
using MillionDance.Utilities;
using OpenTK;
using UnityStudio.UnityEngine;
using UnityStudio.UnityEngine.Animation;
using Quaternion = OpenTK.Quaternion;
using Vector3 = OpenTK.Vector3;
using Vector4 = OpenTK.Vector4;

namespace MillionDance.Core {
    public static class PmxCreator {

        public static PmxModel Create([NotNull] Avatar combinedAvatar, [NotNull] Mesh combinedMesh, int bodyMeshVertexCount, [NotNull] string texturePrefix) {
            var model = new PmxModel();

            model.Name = "ミリシタ モデル00";
            model.NameEnglish = "MODEL_00";
            model.Comment = "製作：MillionDance";
            model.CommentEnglish = "Generated by MillionDance";

            var vertices = AddVertices(combinedAvatar, combinedMesh, bodyMeshVertexCount);
            model.Vertices = vertices;

            var indicies = AddIndices(combinedMesh);
            model.FaceTriangles = indicies;

            var bones = AddBones(combinedAvatar, vertices);
            model.Bones = bones;

#if SKELETON_FORMAT_MMD
#if TRANSLATE_BONE_NAMES_TO_MMD
            FixBonesAndVertices(bones, vertices);
#endif
#elif SKELETON_FORMAT_MLTD
#else
#error You must choose a skeleton format.
#endif

            var materials = AddMaterials(combinedMesh, texturePrefix);
            model.Materials = materials;

            var emotionMorphs = AddEmotionMorphs(combinedMesh);
            model.Morphs = emotionMorphs;

            // PMX Editor requires at least one node (root), or it will crash because these code:
            /**
             * this.PXRootNode = new PXNode(base.RootNode);
             * this.PXExpressionNode = new PXNode(base.ExpNode);
             * this.PXNode.Clear();
             * this.PXNode.Capacity = base.NodeList.Count - 1; // notice this line
             */
            var nodes = AddNodes(bones, emotionMorphs);
            model.Nodes = nodes;

            return model;
        }

        [NotNull, ItemNotNull]
        private static IReadOnlyList<PmxVertex> AddVertices([NotNull] Avatar combinedAvatar, [NotNull] Mesh combinedMesh, int bodyMeshVertexCount) {
            var vertexCount = combinedMesh.VertexCount;
            var vertices = new PmxVertex[vertexCount];

            for (var i = 0; i < vertexCount; ++i) {
                var vertex = new PmxVertex();

                var position = combinedMesh.Vertices[i];
                var normal = combinedMesh.Normals[i];
                var uv = combinedMesh.UV1[i];

                vertex.Position = position.ToOpenTK().FixUnityToOpenTK();

#if SCALE_TO_PMX_SIZE
                vertex.Position = vertex.Position * ConversionConfig.ScaleUnityToMmd;
#endif

                vertex.Normal = normal.ToOpenTK().FixUnityToOpenTK();

                OpenTK.Vector2 fixedUv;

                // Body, then head.
                // TODO: For heads, inverting/flipping is different among models?
                // e.g. ss001_015siz can be processed via the method below; gs001_201xxx's head UVs are not inverted but some are flipped.
                if (i < bodyMeshVertexCount) {
                    // Invert UV!
                    fixedUv = new OpenTK.Vector2(uv.X, 1 - uv.Y);
                } else {
                    fixedUv = uv.ToOpenTK();
                }

                vertex.UV = fixedUv;

                vertex.EdgeScale = 1.0f;

                var skin = combinedMesh.Skin[i];
                var affectiveInfluenceCount = skin.Count(inf => inf != null);

                switch (affectiveInfluenceCount) {
                    case 1:
                        vertex.Deformation = Deformation.Bdef1;
                        break;
                    case 2:
                        vertex.Deformation = Deformation.Bdef2;
                        break;
                    case 3:
                        throw new NotSupportedException();
                    case 4:
                        vertex.Deformation = Deformation.Bdef4;
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(affectiveInfluenceCount));
                }

                for (var j = 0; j < affectiveInfluenceCount; ++j) {
                    var boneId = combinedMesh.BoneNameHashes[skin[j].BoneIndex];
                    var realBoneIndex = combinedAvatar.AvatarSkeleton.NodeIDs.FindIndex(boneId);

                    if (realBoneIndex < 0) {
                        throw new ArgumentOutOfRangeException(nameof(realBoneIndex));
                    }

                    vertex.BoneWeights[j].BoneIndex = realBoneIndex;
                    vertex.BoneWeights[j].Weight = skin[j].Weight;
                }

                vertices[i] = vertex;
            }

            return vertices;
        }

        [NotNull]
        private static IReadOnlyList<int> AddIndices([NotNull] Mesh combinedMesh) {
            var indicies = new int[combinedMesh.Indices.Count];

            for (var i = 0; i < indicies.Length; ++i) {
                indicies[i] = unchecked((int)combinedMesh.Indices[i]);
            }

            return indicies;
        }

        [NotNull, ItemNotNull]
        private static IReadOnlyList<PmxBone> AddBones([NotNull] Avatar combinedAvatar, [NotNull, ItemNotNull] IReadOnlyList<PmxVertex> vertices) {
            var boneCount = combinedAvatar.AvatarSkeleton.NodeIDs.Length;
            var bones = new List<PmxBone>(boneCount);

            var hierachy = BoneUtils.BuildBoneHierarchy(combinedAvatar);

            for (var i = 0; i < boneCount; ++i) {
                var bone = new PmxBone();
                var transform = combinedAvatar.AvatarSkeletonPose.Transforms[i];
                var boneNode = hierachy[i];

                string pmxBoneName;
                var mltdBoneName = boneNode.Path;

#if TRANSLATE_BONE_NAMES_TO_MMD
                if (BoneUtils.BoneNameMap.ContainsKey(mltdBoneName)) {
                    pmxBoneName = BoneUtils.BoneNameMap[mltdBoneName];
                } else {
                    // Prevent the name exceeding max length (15 bytes)
                    pmxBoneName = $"Bone #{mltdBoneName.GetHashCode():x8}";
                }
#else
                pmxBoneName = mltdBoneName;
#endif

                bone.Name = pmxBoneName;
                bone.NameEnglish = BoneUtils.TranslateBoneName(pmxBoneName);

                // PMX's bone positions are in world coordinate system.
                // Unity's are in local coords.
                bone.InitialPosition = boneNode.InitialPositionWorld;
                bone.CurrentPosition = bone.InitialPosition;

                bone.ParentIndex = boneNode.Parent?.Index ?? -1;
                bone.BoneIndex = i;

                var singleDirectChild = boneNode.GetDirectSingleChild();

                if (singleDirectChild != null) {
                    bone.SetFlag(BoneFlags.ToBone);
                    bone.To_Bone = singleDirectChild.Index;
                } else {
                    // TODO: Fix this; it should point to a world position.
                    bone.To_Offset = transform.Translation.ToOpenTK().FixUnityToOpenTK();
                }

                // No use. This is just a flag to specify more details to rotation/translation limitation.
                //bone.SetFlag(BoneFlags.LocalFrame);
                bone.InitialRotation = transform.Rotation.ToOpenTK().FixUnityToOpenTK();
                bone.CurrentRotation = bone.InitialRotation;

                //bone.Level = boneNode.Level;
                bone.Level = 0;

                if (MovableBoneNames.Contains(mltdBoneName)) {
                    bone.SetFlag(BoneFlags.Rotation | BoneFlags.Translation);
                } else {
                    bone.SetFlag(BoneFlags.Rotation);
                }

#if HIDE_UNITY_GENERATED_BONES
                if (IsNameGeneratedName(boneNode.Path)) {
                    bone.ClearFlag(BoneFlags.Visible);
                }
#endif

                bones.Add(bone);
            }

#if FIX_MMD_CENTER_BONES
            // Add master (全ての親) and center (センター), recompute bone hierarchy.
            do {
                PmxBone master = new PmxBone(), center = new PmxBone();

                master.Name = "全ての親";
                master.NameEnglish = "master";
                center.Name = "センター";
                center.NameEnglish = "center";

                master.ParentIndex = 0; // "" bone
                center.ParentIndex = 1; // "master" bone

                master.CurrentPosition = master.InitialPosition = Vector3.Zero;
                center.CurrentPosition = center.InitialPosition = Vector3.Zero;

                master.SetFlag(BoneFlags.Translation | BoneFlags.Rotation);
                center.SetFlag(BoneFlags.Translation | BoneFlags.Rotation);

                bones.Insert(1, master);
                bones.Insert(2, center);

                //// Fix "MODEL_00" bone

                //do {
                //    var model00 = bones.Find(b => b.Name == "グルーブ");

                //    if (model00 == null) {
                //        throw new ArgumentException("MODEL_00 mapped bone is not found.");
                //    }

                //    model00.ParentIndex = 2; // "center" bone
                //} while (false);

                const int numBonesAdded = 2;

                // Fix vertices and other bones
                foreach (var vertex in vertices) {
                    foreach (var boneWeight in vertex.BoneWeights) {
                        if (boneWeight.BoneIndex == 0 && boneWeight.Weight <= 0) {
                            continue;
                        }

                        if (boneWeight.BoneIndex >= 1) {
                            boneWeight.BoneIndex += numBonesAdded;
                        }
                    }
                }

                for (var i = numBonesAdded + 1; i < bones.Count; ++i) {
                    var bone = bones[i];

                    bone.ParentIndex += numBonesAdded;

                    if (bone.HasFlag(BoneFlags.ToBone)) {
                        bone.To_Bone += numBonesAdded;
                    }
                }
            } while (false);
#endif

#if APPEND_IK_BONES
            // Add IK bones.
            do {
                PmxBone[] CreateLegIK(string leftRightJp, string leftRightEn) {
                    var startBoneCount = bones.Count;

                    PmxBone ikParent = new PmxBone(), ikBone = new PmxBone();

                    ikParent.Name = leftRightJp + "足IK親";
                    ikParent.NameEnglish = "leg IKP_" + leftRightEn;
                    ikBone.Name = leftRightJp + "足ＩＫ";
                    ikBone.NameEnglish = "leg IK_" + leftRightEn;

                    PmxBone master;

                    do {
                        master = bones.Find(b => b.Name == "全ての親");

                        if (master == null) {
                            throw new ArgumentException("Missing master bone.");
                        }
                    } while (false);

                    ikParent.ParentIndex = bones.IndexOf(master);
                    ikBone.ParentIndex = startBoneCount; // IKP
                    ikParent.SetFlag(BoneFlags.ToBone);
                    ikBone.SetFlag(BoneFlags.ToBone);
                    ikParent.To_Bone = startBoneCount + 1; // IK
                    ikBone.To_Bone = -1;

                    PmxBone ankle, knee, leg;

                    do {
                        var ankleName = leftRightJp + "足首";
                        ankle = bones.Find(b => b.Name == ankleName);
                        var kneeName = leftRightJp + "ひざ";
                        knee = bones.Find(b => b.Name == kneeName);
                        var legName = leftRightJp + "足";
                        leg = bones.Find(b => b.Name == legName);

                        if (ankle == null) {
                            throw new ArgumentException("Missing ankle bone.");
                        }

                        if (knee == null) {
                            throw new ArgumentException("Missing knee bone.");
                        }

                        if (leg == null) {
                            throw new ArgumentException("Missing leg bone.");
                        }
                    } while (false);

                    ikBone.CurrentPosition = ikBone.InitialPosition = ankle.InitialPosition;
                    ikParent.CurrentPosition = ikParent.InitialPosition = new Vector3(ikBone.InitialPosition.X, 0, ikBone.InitialPosition.Z);

                    ikParent.SetFlag(BoneFlags.Translation | BoneFlags.Rotation);
                    ikBone.SetFlag(BoneFlags.Translation | BoneFlags.Rotation | BoneFlags.IK);

                    var ik = new PmxIK();

                    ik.LoopCount = 10;
                    ik.AngleLimit = MathHelper.DegreesToRadians(114.5916f);
                    ik.TargetBoneIndex = bones.IndexOf(ankle);

                    var links = new IKLink[2];

                    links[0] = new IKLink();
                    links[0].BoneIndex = bones.IndexOf(knee);
                    links[0].IsLimited = true;
                    links[0].LowerBound = new Vector3(MathHelper.DegreesToRadians(-180), 0, 0);
                    links[0].UpperBound = new Vector3(MathHelper.DegreesToRadians(-0.5f), 0, 0);
                    links[1] = new IKLink();
                    links[1].BoneIndex = bones.IndexOf(leg);

                    ik.Links = links;
                    ikBone.IK = ik;

                    return new[] {
                            ikParent, ikBone
                        };
                }

                PmxBone[] CreateToeIK(string leftRightJp, string leftRightEn) {
                    PmxBone ikParent, ikBone = new PmxBone();

                    do {
                        var parentName = leftRightJp + "足ＩＫ";

                        ikParent = bones.Find(b => b.Name == parentName);

                        Debug.Assert(ikParent != null, nameof(ikParent) + " != null");
                    } while (false);

                    ikBone.Name = leftRightJp + "つま先ＩＫ";
                    ikBone.NameEnglish = "toe IK_" + leftRightEn;

                    ikBone.ParentIndex = bones.IndexOf(ikParent);

                    ikBone.SetFlag(BoneFlags.ToBone);
                    ikBone.To_Bone = -1;

                    PmxBone toe, ankle;

                    do {
                        var toeName = leftRightJp + "つま先";
                        toe = bones.Find(b => b.Name == toeName);
                        var ankleName = leftRightJp + "足首";
                        ankle = bones.Find(b => b.Name == ankleName);

                        if (toe == null) {
                            throw new ArgumentException("Missing toe bone.");
                        }

                        if (ankle == null) {
                            throw new ArgumentException("Missing ankle bone.");
                        }
                    } while (false);

                    ikBone.CurrentPosition = ikBone.InitialPosition = toe.InitialPosition;
                    ikBone.SetFlag(BoneFlags.Translation | BoneFlags.Rotation | BoneFlags.IK);

                    var ik = new PmxIK();

                    ik.LoopCount = 10;
                    ik.AngleLimit = MathHelper.DegreesToRadians(114.5916f);
                    ik.TargetBoneIndex = bones.IndexOf(toe);

                    var links = new IKLink[1];

                    links[0] = new IKLink();
                    links[0].BoneIndex = bones.IndexOf(ankle);

                    ik.Links = links.ToArray();
                    ikBone.IK = ik;

                    return new[] {
                            ikBone
                        };
                }

                var leftLegIK = CreateLegIK("左", "L");
                bones.AddRange(leftLegIK);
                var rightLegIK = CreateLegIK("右", "R");
                bones.AddRange(rightLegIK);

                var leftToeIK = CreateToeIK("左", "L");
                bones.AddRange(leftToeIK);
                var rightToeIK = CreateToeIK("右", "R");
                bones.AddRange(rightToeIK);
            } while (false);
#endif

            // Finally, set the indices. The values will be used later.
            for (var i = 0; i < bones.Count; i++) {
                bones[i].BoneIndex = i;
            }

            return bones.ToArray();
        }

        // Change T-pose to A-pose
        private static void FixBonesAndVertices([NotNull, ItemNotNull] IReadOnlyList<PmxBone> bones, [NotNull, ItemNotNull] IReadOnlyList<PmxVertex> vertices) {
            var defRotRight = Quaternion.FromEulerAngles(0, 0, MathHelper.DegreesToRadians(37.5f));
            var defRotLeft = Quaternion.FromEulerAngles(0, 0, MathHelper.DegreesToRadians(-37.5f));

            var leftArm = bones.SingleOrDefault(b => b.Name == "左腕");
            var rightArm = bones.SingleOrDefault(b => b.Name == "右腕");

            Debug.Assert(leftArm != null, nameof(leftArm) + " != null");
            Debug.Assert(rightArm != null, nameof(rightArm) + " != null");

            leftArm.AnimatedRotation = defRotLeft;
            rightArm.AnimatedRotation = defRotRight;

            foreach (var bone in bones) {
                if (bone.ParentIndex >= 0) {
                    bone.Parent = bones[bone.ParentIndex];
                }
            }

            foreach (var bone in bones) {
                bone.SetToVmdPose(true);
            }

            foreach (var vertex in vertices) {
                var m = Matrix4.Zero;

                for (var j = 0; j < 4; ++j) {
                    var boneWeight = vertex.BoneWeights[j];

                    if (!boneWeight.IsValid) {
                        continue;
                    }

                    m = m + bones[boneWeight.BoneIndex].SkinMatrix * boneWeight.Weight;
                }

                vertex.Position = Vector3.TransformPosition(vertex.Position, m);
                vertex.Normal = Vector3.TransformNormal(vertex.Normal, m);
            }

            foreach (var bone in bones) {
                bone.InitialPosition = bone.CurrentPosition;
            }
        }

        [NotNull, ItemNotNull]
        private static IReadOnlyList<PmxMaterial> AddMaterials([NotNull] Mesh combinedMesh, [NotNull] string texturePrefix) {
            var materialCount = combinedMesh.SubMeshes.Count;
            var materials = new PmxMaterial[materialCount];

            for (var i = 0; i < materialCount; ++i) {
                var material = new PmxMaterial();

                material.NameEnglish = material.Name = $"Mat #{i:00}";
                material.AppliedFaceVertexCount = (int)combinedMesh.SubMeshes[i].IndexCount;
                material.Ambient = Vector3.One;
                material.Diffuse = Vector4.One;
                material.Specular = Vector3.Zero;
                material.EdgeColor = new Vector4(0, 0, 0, 1);
                material.EdgeSize = 1.0f;
                // TODO: The right way: reading textures' path ID and do the mapping.
                material.TextureFileName = $"{texturePrefix}{i:00}.png";

                material.Flags = MaterialFlags.Shadow | MaterialFlags.SelfShadow;

                materials[i] = material;
            }

            return materials;
        }

        [NotNull, ItemNotNull]
        private static IReadOnlyList<PmxMorph> AddEmotionMorphs([NotNull] Mesh mesh) {
            var morphs = new List<PmxMorph>();

            var s = mesh.Shape;

            if (s != null) {
                Debug.Assert(s.Channels.Count == s.Shapes.Count);
                Debug.Assert(s.Channels.Count == s.FullWeights.Count);

                var morphCount = s.Channels.Count;

                for (var i = 0; i < morphCount; i++) {
                    var channel = s.Channels[i];
                    var shape = s.Shapes[i];
                    var vertices = s.Vertices;
                    var morph = new PmxMorph();

                    morph.Name = MorphUtils.LookupMorphName(channel.Name);
                    morph.NameEnglish = channel.Name;

                    morph.OffsetKind = MorphOffsetKind.Vertex;

                    var offsets = new List<PmxBaseMorph>();

                    for (var j = shape.FirstVertex; j < shape.FirstVertex + shape.VertexCount; ++j) {
                        var v = vertices[(int)j];
                        var m = new PmxVertexMorph();

                        var offset = v.Vertex.ToOpenTK().FixUnityToOpenTK();

#if SCALE_TO_PMX_SIZE
                        offset = offset * ConversionConfig.ScaleUnityToMmd;
#endif  

                        m.Index = (int)v.Index;
                        m.Offset = offset;

                        offsets.Add(m);
                    }

                    morph.Offsets = offsets.ToArray();

                    morphs.Add(morph);
                }

                // Now some custom morphs for our model to be compatible with TDA.
                do {
                    PmxMorph CreateCompositeMorph(string morphName, params string[] names) {
                        int FindIndex<T>(IReadOnlyList<T> list, T item) {
                            var comparer = EqualityComparer<T>.Default;

                            for (var i = 0; i < list.Count; ++i) {
                                if (comparer.Equals(item, list[i])) {
                                    return i;
                                }
                            }

                            return -1;
                        }

                        var morph = new PmxMorph();

                        morph.Name = MorphUtils.LookupMorphName(morphName);
                        morph.NameEnglish = morphName;

                        var offsets = new List<PmxBaseMorph>();
                        var vertices = s.Vertices;

                        foreach (var channel in names.Select(name => s.Channels.Single(ch => ch.Name == name))) {
                            var channelIndex = FindIndex(s.Channels, channel);
                            var shape = s.Shapes[channelIndex];

                            morph.OffsetKind = MorphOffsetKind.Vertex;

                            for (var j = shape.FirstVertex; j < shape.FirstVertex + shape.VertexCount; ++j) {
                                var v = vertices[(int)j];
                                var m = new PmxVertexMorph();

                                var offset = v.Vertex.ToOpenTK().FixUnityToOpenTK();

#if SCALE_TO_PMX_SIZE
                                offset = offset * ConversionConfig.ScaleUnityToMmd;
#endif

                                m.Index = (int)v.Index;
                                m.Offset = offset;

                                offsets.Add(m);
                            }
                        }

                        morph.Offsets = offsets.ToArray();

                        return morph;
                    }

                    morphs.Add(CreateCompositeMorph("blendShape1.E_metoji", "blendShape1.E_metoji_l", "blendShape1.E_metoji_r"));
                } while (false);
            }

            return morphs.ToArray();
        }

        [NotNull, ItemNotNull]
        private static IReadOnlyList<PmxNode> AddNodes([NotNull, ItemNotNull] IReadOnlyList<PmxBone> bones, [NotNull, ItemNotNull] IReadOnlyList<PmxMorph> morphs) {
            var nodes = new List<PmxNode>();

            PmxNode CreateBoneGroup(string groupNameJp, string groupNameEn, params string[] boneNames) {
                var node = new PmxNode();

                node.Name = groupNameJp;
                node.NameEnglish = groupNameEn;

                var boneNodes = new List<NodeElement>();

                foreach (var boneName in boneNames) {
                    var bone = bones.SingleOrDefault(b => b.Name == boneName);

                    if (bone != null) {
                        boneNodes.Add(new NodeElement {
                            ElementType = ElementType.Bone,
                            Index = bone.BoneIndex
                        });
                    } else {
                        Debug.Print("Warning: bone node not found: {0}", boneName);
                    }
                }

                node.Elements = boneNodes.ToArray();

                return node;
            }

            PmxNode CreateEmotionNode() {
                var node = new PmxNode();

                node.Name = "表情";
                node.NameEnglish = "Facial Expressions";

                var elements = new List<NodeElement>();

                var counter = 0;

                foreach (var _ in morphs) {
                    var elem = new NodeElement();

                    elem.ElementType = ElementType.Morph;
                    elem.Index = counter;

                    elements.Add(elem);

                    ++counter;
                }

                node.Elements = elements.ToArray();

                return node;
            }

            nodes.Add(CreateBoneGroup("Root", "Root", "操作中心"));
            nodes.Add(CreateEmotionNode());
            nodes.Add(CreateBoneGroup("センター", "center", "全ての親", "センター"));
            nodes.Add(CreateBoneGroup("ＩＫ", "IK", "左足IK親", "左足ＩＫ", "左つま先ＩＫ", "右足IK親", "右足ＩＫ", "右つま先ＩＫ"));
            nodes.Add(CreateBoneGroup("体(上)", "Upper Body", "上半身", "上半身2", "首", "頭"));
            nodes.Add(CreateBoneGroup("腕", "Arms", "左肩", "左腕", "左ひじ", "左手首", "右肩", "右腕", "右ひじ", "右手首"));
            nodes.Add(CreateBoneGroup("手", "Hands", "左親指１", "左親指２", "左親指３", "左人指１", "左人指２", "左人指３", "左ダミー", "左中指１", "左中指２", "左中指３", "左薬指１", "左薬指２", "左薬指３", "左小指１", "左小指２", "左小指３",
                "右親指１", "右親指２", "右親指３", "右人指１", "右人指２", "右人指３", "右ダミー", "右中指１", "右中指２", "右中指３", "右薬指１", "右薬指２", "右薬指３", "右小指１", "右小指２", "右小指３"));
            nodes.Add(CreateBoneGroup("体(下)", "Lower Body", "グルーブ", "腰", "下半身"));
            nodes.Add(CreateBoneGroup("足", "Legs", "左足", "左ひざ", "左足首", "左つま先", "右足", "右ひざ", "右足首", "右つま先"));

            return nodes.ToArray();
        }

        [CanBeNull]
        private static BoneNode GetDirectSingleChild([NotNull] this BoneNode b) {
            var l = new List<BoneNode>();

            foreach (var c in b.Children) {
                var isGenerated = IsNameGeneratedName(c.Path);

                if (!isGenerated) {
                    l.Add(c);
                }
            }

            if (l.Count == 1) {
                return l[0];
            } else {
                return null;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool IsNameGeneratedName([NotNull] string name) {
            return CompilerGeneratedJointParts.Any(name.Contains);
        }

        [NotNull, ItemNotNull]
        private static readonly ISet<string> MovableBoneNames = new HashSet<string> {
            "",
            "POSITION",
            "MODEL_00",
            "MODEL_00/BASE"
        };

        [NotNull, ItemNotNull]
        private static readonly ISet<string> CompilerGeneratedJointParts = new HashSet<string> {
            "__rot",
            "__null",
            "__const",
            "__twist",
            "__slerp"
        };

    }
}
