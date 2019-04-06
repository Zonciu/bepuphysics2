﻿using BepuPhysics.Collidables;
using BepuUtilities;
using System;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace BepuPhysics.CollisionDetection.CollisionTasks
{
    public struct ConvexHullPairTester : IPairTester<ConvexHullWide, ConvexHullWide, Convex4ContactManifoldWide>
    {
        struct CachedEdge
        {
            public Vector3 Vertex;
            public Vector3 EdgePlaneNormal;
            public float MaximumContainmentDot;
        }
        struct CachedEdgeWide
        {
            public Vector3Wide Vertex;
            public Vector3Wide EdgePlaneNormal;
            public Vector<float> MaximumContainmentDot;
        }
        public int BatchSize => 32;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public unsafe void Test(ref ConvexHullWide a, ref ConvexHullWide b, ref Vector<float> speculativeMargin, ref Vector3Wide offsetB, ref QuaternionWide orientationA, ref QuaternionWide orientationB, int pairCount, out Convex4ContactManifoldWide manifold)
        {
            Matrix3x3Wide.CreateFromQuaternion(orientationA, out var rA);
            Matrix3x3Wide.CreateFromQuaternion(orientationB, out var rB);
            Matrix3x3Wide.MultiplyByTransposeWithoutOverlap(rA, rB, out var bLocalOrientationA);
            ref var localCapsuleAxis = ref bLocalOrientationA.Y;

            Matrix3x3Wide.TransformByTransposedWithoutOverlap(offsetB, rB, out var localOffsetB);
            Vector3Wide.Negate(localOffsetB, out var localOffsetA);
            Vector3Wide.Length(localOffsetA, out var centerDistance);
            Vector3Wide.Scale(localOffsetA, Vector<float>.One / centerDistance, out var initialNormal);
            var useInitialFallback = Vector.LessThan(centerDistance, new Vector<float>(1e-8f));
            initialNormal.X = Vector.ConditionalSelect(useInitialFallback, Vector<float>.Zero, initialNormal.X);
            initialNormal.Y = Vector.ConditionalSelect(useInitialFallback, Vector<float>.One, initialNormal.Y);
            initialNormal.Z = Vector.ConditionalSelect(useInitialFallback, Vector<float>.Zero, initialNormal.Z);
            var hullSupportFinder = default(ConvexHullSupportFinder);
            ManifoldCandidateHelper.CreateInactiveMask(pairCount, out var inactiveLanes);
            a.EstimateEpsilonScale(inactiveLanes, out var aEpsilonScale);
            b.EstimateEpsilonScale(inactiveLanes, out var bEpsilonScale);
            var epsilonScale = Vector.Min(aEpsilonScale, bEpsilonScale);
            var depthThreshold = -speculativeMargin;
            DepthRefiner<ConvexHull, ConvexHullWide, ConvexHullSupportFinder, ConvexHull, ConvexHullWide, ConvexHullSupportFinder>.FindMinimumDepth(
                b, a, localOffsetA, bLocalOrientationA, ref hullSupportFinder, ref hullSupportFinder, initialNormal, inactiveLanes, 1e-5f * epsilonScale, depthThreshold,
                out var depth, out var localNormal, out var closestOnB);

            inactiveLanes = Vector.BitwiseOr(inactiveLanes, Vector.LessThan(depth, depthThreshold));
            //Not every lane will generate contacts. Rather than requiring every lane to carefully clear all contactExists states, just clear them up front.
            manifold.Contact0Exists = default;
            manifold.Contact1Exists = default;
            manifold.Contact2Exists = default;
            manifold.Contact3Exists = default;
            if (Vector.LessThanAll(inactiveLanes, Vector<int>.Zero))
            {
                //No contacts generated.
                return;
            }

            Matrix3x3Wide.TransformByTransposedWithoutOverlap(localNormal, bLocalOrientationA, out var localNormalInA);
            Vector3Wide.Negate(localNormalInA, out var negatedLocalNormalInA);
            Vector3Wide.Scale(localNormal, depth, out var negatedOffsetToClosestOnA);
            Vector3Wide.Subtract(closestOnB, negatedOffsetToClosestOnA, out var closestOnA);
            Vector3Wide.Subtract(closestOnA, localOffsetA, out var aToClosestOnA);
            Matrix3x3Wide.TransformByTransposedWithoutOverlap(aToClosestOnA, bLocalOrientationA, out var closestOnAInA);

            //To find the contact manifold, we'll clip the capsule axis against the face as usual, but we're dealing with potentially
            //distinct convex hulls. Rather than vectorizing over the different hulls, we vectorize within each hull.
            Helpers.FillVectorWithLaneIndices(out var slotOffsetIndices);
            var boundingPlaneEpsilon = 1e-3f * epsilonScale;


            //TODO: If you end up using vectorized postpass, this has to be updated.
            var wideCandidatesBytes = stackalloc byte[4096];
            ref var wideCandidates = ref Unsafe.As<byte, ManifoldCandidate>(ref *wideCandidatesBytes);
            Vector<int> wideCandidateCount = default;
            Vector3Wide bFaceXBundle = default, bFaceYBundle = default, faceNormalABundle = default, aFaceOriginBundle = default, bFaceOriginBundle = default;

            for (int slotIndex = 0; slotIndex < pairCount; ++slotIndex)
            {
                if (inactiveLanes[slotIndex] < 0)
                    continue;
                ref var aSlot = ref a.Hulls[slotIndex];
                ref var bSlot = ref b.Hulls[slotIndex];
                ConvexHullTestHelper.PickRepresentativeFace(ref aSlot, slotIndex, ref negatedLocalNormalInA, closestOnAInA, slotOffsetIndices, ref boundingPlaneEpsilon, out var slotFaceNormalAInA, out _, out var bestFaceIndexA);
                Matrix3x3Wide.ReadSlot(ref bLocalOrientationA, slotIndex, out var slotBLocalOrientationA);
                Matrix3x3.Transform(slotFaceNormalAInA, slotBLocalOrientationA, out var slotFaceNormalA);
                Vector3Wide.ReadSlot(ref localOffsetA, slotIndex, out var slotLocalOffsetA);
                ConvexHullTestHelper.PickRepresentativeFace(ref bSlot, slotIndex, ref localNormal, closestOnB, slotOffsetIndices, ref boundingPlaneEpsilon, out var slotFaceNormalB, out var slotLocalNormal, out var bestFaceIndexB);
                Helpers.BuildOrthnormalBasis(slotFaceNormalB, out var bFaceX, out var bFaceY);

                //Test each face edge plane against the capsule edge.
                //Note that we do not use the faceNormal x edgeOffset edge plane, but rather edgeOffset x localNormal.
                //(In other words, testing the *projected* capsule axis on the surface of the convex hull face.)
                //The faces are wound counterclockwise.
                aSlot.GetVertexIndicesForFace(bestFaceIndexA, out var faceVertexIndicesA);
                bSlot.GetVertexIndicesForFace(bestFaceIndexB, out var faceVertexIndicesB);

                //Create cached edge data for A.
                var cachedEdges = stackalloc CachedEdge[faceVertexIndicesA.Length];
                var previousIndexA = faceVertexIndicesA[faceVertexIndicesA.Length - 1];
                Vector3Wide.ReadSlot(ref aSlot.Points[previousIndexA.BundleIndex], previousIndexA.InnerIndex, out var previousVertexA);
                Matrix3x3.Transform(previousVertexA, slotBLocalOrientationA, out previousVertexA);
                previousVertexA += slotLocalOffsetA;
                for (int i = 0; i < faceVertexIndicesA.Length; ++i)
                {
                    ref var edge = ref cachedEdges[i];
                    edge.MaximumContainmentDot = float.MinValue;
                    var indexA = faceVertexIndicesA[i];
                    Vector3Wide.ReadSlot(ref aSlot.Points[indexA.BundleIndex], indexA.InnerIndex, out edge.Vertex);
                    Matrix3x3.Transform(edge.Vertex, slotBLocalOrientationA, out edge.Vertex);
                    edge.Vertex += slotLocalOffsetA;
                    //Note flipped cross order; local normal points from B to A.
                    Vector3x.Cross(slotLocalNormal, edge.Vertex - previousVertexA, out edge.EdgePlaneNormal);
                    previousVertexA = edge.Vertex;
                }
                var maximumCandidateCount = faceVertexIndicesB.Length * 2; //Two contacts per edge.
                var candidates = stackalloc ManifoldCandidateScalar[maximumCandidateCount];
                var candidateCount = 0;
                var previousIndexB = faceVertexIndicesB[faceVertexIndicesB.Length - 1];
                //Clip face B's edges against A's face, and test A's vertices against B's face.
                //We use B's face as the contact surface to be consistent with the other pairs in case we end up implementing a HullCollectionReduction similar to MeshReduction.
                Vector3Wide.ReadSlot(ref bSlot.Points[previousIndexB.BundleIndex], previousIndexB.InnerIndex, out var bFaceOrigin);
                var previousVertexB = bFaceOrigin;
                for (int faceVertexIndexB = 0; faceVertexIndexB < faceVertexIndicesB.Length; ++faceVertexIndexB)
                {
                    var indexB = faceVertexIndicesB[faceVertexIndexB];
                    Vector3Wide.ReadSlot(ref bSlot.Points[indexB.BundleIndex], indexB.InnerIndex, out var vertexB);

                    var edgeOffsetB = vertexB - previousVertexB;
                    Vector3x.Cross(edgeOffsetB, slotLocalNormal, out var edgePlaneNormalB);

                    var latestEntryNumerator = float.MaxValue;
                    var latestEntryDenominator = -1f;
                    var earliestExitNumerator = float.MaxValue;
                    var earliestExitDenominator = 1f;
                    for (int faceVertexIndexA = 0; faceVertexIndexA < faceVertexIndicesA.Length; ++faceVertexIndexA)
                    {
                        ref var edgeA = ref cachedEdges[faceVertexIndexA];

                        //Check containment in this B edge.
                        var edgeBToEdgeA = edgeA.Vertex - previousVertexB;
                        var containmentDot = Vector3.Dot(edgeBToEdgeA, edgePlaneNormalB);
                        if (edgeA.MaximumContainmentDot < containmentDot)
                            edgeA.MaximumContainmentDot = containmentDot;

                        //t = dot(pointOnEdgeA - pointOnEdgeB, edgePlaneNormalA) / dot(edgePlaneNormalA, edgeOffsetB)
                        //Note that we can defer the division; we don't need to compute the exact t value of *all* planes.

                        var numerator = Vector3.Dot(edgeBToEdgeA, edgeA.EdgePlaneNormal);
                        var denominator = Vector3.Dot(edgeA.EdgePlaneNormal, edgeOffsetB);

                        //A plane is being 'entered' if the ray direction opposes the face normal.
                        //Entry denominators are always negative, exit denominators are always positive. Don't have to worry about comparison sign flips.
                        if (denominator < 0)
                        {
                            if (numerator * latestEntryDenominator > latestEntryNumerator * denominator)
                            {
                                latestEntryNumerator = numerator;
                                latestEntryDenominator = denominator;
                            }
                        }
                        else if (denominator > 0)
                        {
                            if (numerator * earliestExitDenominator < earliestExitNumerator * denominator)
                            {
                                earliestExitNumerator = numerator;
                                earliestExitDenominator = denominator;
                            }
                        }
                    }
                    //We now have bounds on B's edge.
                    //Denominator signs are opposed; comparison flipped.
                    if (earliestExitNumerator * latestEntryDenominator <= latestEntryNumerator * earliestExitDenominator)
                    {
                        //This edge of B was actually contained in A's face. Add contacts for it.
                        var latestEntry = latestEntryNumerator / latestEntryDenominator;
                        var earliestExit = earliestExitNumerator / earliestExitDenominator;
                        latestEntry = latestEntry < 0 ? 0 : latestEntry;
                        earliestExit = earliestExit > 1 ? 1 : earliestExit;
                        //Create max contact if max >= min.
                        //Create min if min < max and min > 0.
                        var startId = (previousIndexB.BundleIndex << BundleIndexing.VectorShift) + previousIndexB.InnerIndex;
                        var endId = (indexB.BundleIndex << BundleIndexing.VectorShift) + indexB.InnerIndex;
                        var baseFeatureId = (startId ^ endId) << 8;
                        if (earliestExit >= latestEntry && candidateCount < maximumCandidateCount)
                        {
                            //Create max contact.
                            var point = edgeOffsetB * earliestExit + previousVertexB - bFaceOrigin;
                            var newContactIndex = candidateCount++;
                            ref var candidate = ref candidates[newContactIndex];
                            candidate.X = Vector3.Dot(point, bFaceX);
                            candidate.Y = Vector3.Dot(point, bFaceY);
                            candidate.FeatureId = baseFeatureId + endId;
                        }
                        if (latestEntry < earliestExit && latestEntry > 0 && candidateCount < maximumCandidateCount)
                        {
                            //Create min contact.
                            var point = edgeOffsetB * latestEntry + previousVertexB - bFaceOrigin;
                            var newContactIndex = candidateCount++;
                            ref var candidate = ref candidates[newContactIndex];
                            candidate.X = Vector3.Dot(point, bFaceX);
                            candidate.Y = Vector3.Dot(point, bFaceY);
                            candidate.FeatureId = baseFeatureId + startId;
                        }
                    }
                    previousIndexB = indexB;
                    previousVertexB = vertexB;
                }
                //We've now analyzed every edge of B. Check for vertices from A to add.
                var inverseFaceNormalADotFaceNormalB = 1f / Vector3.Dot(slotLocalNormal, slotFaceNormalB);
                for (int i = 0; i < faceVertexIndicesA.Length && candidateCount < maximumCandidateCount; ++i)
                {
                    ref var edge = ref cachedEdges[i];
                    if (edge.MaximumContainmentDot <= 0)
                    {
                        //This vertex was contained by all b edge plane normals. Include it.
                        //Project it onto B's surface:
                        //vertexA - localNormal * dot(vertexA - faceOriginB, faceNormalB) / dot(localNormal, faceNormalB); 
                        var bFaceToVertexA = edge.Vertex - bFaceOrigin;
                        var distance = Vector3.Dot(bFaceToVertexA, slotFaceNormalB) * inverseFaceNormalADotFaceNormalB;
                        var bFaceToProjectedVertexA = bFaceToVertexA - slotLocalNormal * distance;

                        var newContactIndex = candidateCount++;
                        ref var candidate = ref candidates[newContactIndex];
                        candidate.X = Vector3.Dot(bFaceX, bFaceToProjectedVertexA);
                        candidate.Y = Vector3.Dot(bFaceY, bFaceToProjectedVertexA);
                        candidate.FeatureId = i;
                    }
                }
                //Store the slot's data for vectorized post processing.
                GatherScatter.GetFirst(ref wideCandidateCount) = candidateCount;

                ref var baseWideCandidate = ref GatherScatter.GetOffsetInstance(ref wideCandidates, slotIndex);
                for (int i = 0; i < candidateCount; ++i)
                {
                    ref var candidateWide = ref Unsafe.Add(ref baseWideCandidate, i);
                    ref var candidate = ref candidates[i];
                    GatherScatter.GetFirst(ref candidateWide.X) = candidate.X;
                    GatherScatter.GetFirst(ref candidateWide.Y) = candidate.Y;
                    GatherScatter.GetFirst(ref candidateWide.FeatureId) = candidate.FeatureId;
                }
                Vector3Wide.WriteSlot(slotFaceNormalA, slotIndex, ref faceNormalABundle);
                Vector3Wide.WriteSlot(cachedEdges[0].Vertex, slotIndex, ref aFaceOriginBundle);
                Vector3Wide.WriteSlot(bFaceOrigin, slotIndex, ref bFaceOriginBundle);
                Vector3Wide.WriteSlot(bFaceX, slotIndex, ref bFaceXBundle);
                Vector3Wide.WriteSlot(bFaceY, slotIndex, ref bFaceYBundle);


                //Matrix3x3Wide.ReadSlot(ref rB, slotIndex, out var slotOrientationB);
                //Vector3Wide.ReadSlot(ref offsetB, slotIndex, out var slotOffsetB);
                //ManifoldCandidateHelper.Reduce(candidates, candidateCount, slotFaceNormalA, slotLocalNormal, cachedEdges[0].Vertex, bFaceOrigin, bFaceX, bFaceY, epsilonScale[slotIndex], depthThreshold[slotIndex], slotOrientationB, slotOffsetB, slotIndex, ref manifold);

            }

            var maximumCandidateCountAcrossAllSlots = wideCandidateCount[0];
            for (int i = 1; i < Vector<float>.Count; ++i)
            {
                var candidateCount = wideCandidateCount[i];
                if (maximumCandidateCountAcrossAllSlots < candidateCount)
                    maximumCandidateCountAcrossAllSlots = candidateCount;
            }
            Vector3Wide.Subtract(aFaceOriginBundle, bFaceOriginBundle, out var faceCenterBToFaceCenterABundle);
            ManifoldCandidateHelper.Reduce(ref wideCandidates, wideCandidateCount, maximumCandidateCountAcrossAllSlots, faceNormalABundle, localNormal, faceCenterBToFaceCenterABundle, bFaceXBundle, bFaceYBundle, epsilonScale, depthThreshold, pairCount,
                out var contact0, out var contact1, out var contact2, out var contact3, out manifold.Contact0Exists, out manifold.Contact1Exists, out manifold.Contact2Exists, out manifold.Contact3Exists);

            TransformContact(bFaceOriginBundle, bFaceXBundle, bFaceYBundle, localOffsetA, rB, contact0, out manifold.OffsetA0, out manifold.Depth0, out manifold.FeatureId0);
            TransformContact(bFaceOriginBundle, bFaceXBundle, bFaceYBundle, localOffsetA, rB, contact1, out manifold.OffsetA1, out manifold.Depth1, out manifold.FeatureId1);
            TransformContact(bFaceOriginBundle, bFaceXBundle, bFaceYBundle, localOffsetA, rB, contact2, out manifold.OffsetA2, out manifold.Depth2, out manifold.FeatureId2);
            TransformContact(bFaceOriginBundle, bFaceXBundle, bFaceYBundle, localOffsetA, rB, contact3, out manifold.OffsetA3, out manifold.Depth3, out manifold.FeatureId3);

            Matrix3x3Wide.TransformWithoutOverlap(localNormal, rB, out manifold.Normal);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void TransformContact(in Vector3Wide bFaceOrigin, in Vector3Wide bFaceX, in Vector3Wide bFaceY, in Vector3Wide localOffsetA, in Matrix3x3Wide rB, in ManifoldCandidate candidate,
            out Vector3Wide offsetA, out Vector<float> depth, out Vector<int> featureId)
        {
            Vector3Wide.Scale(bFaceX, candidate.X, out var x);
            Vector3Wide.Scale(bFaceY, candidate.Y, out var y);
            Vector3Wide.Add(x, y, out var offsetFromFaceOriginB);
            Vector3Wide.Add(offsetFromFaceOriginB, bFaceOrigin, out var localContact);
            Vector3Wide.Subtract(localContact, localOffsetA, out localContact);
            Matrix3x3Wide.TransformWithoutOverlap(localContact, rB, out offsetA);
            depth = candidate.Depth;
            featureId = candidate.FeatureId;
        }

        public void Test(ref ConvexHullWide a, ref ConvexHullWide b, ref Vector<float> speculativeMargin, ref Vector3Wide offsetB, ref QuaternionWide orientationB, int pairCount, out Convex4ContactManifoldWide manifold)
        {
            throw new NotImplementedException();
        }

        public void Test(ref ConvexHullWide a, ref ConvexHullWide b, ref Vector<float> speculativeMargin, ref Vector3Wide offsetB, int pairCount, out Convex4ContactManifoldWide manifold)
        {
            throw new NotImplementedException();
        }
    }
}
