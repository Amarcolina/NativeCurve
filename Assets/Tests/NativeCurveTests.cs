using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;
using UnityEngine.TestTools;

public class NativeCurveTests {

    private const float TOLERANCE = 0.0001f;

    private AnimationCurve curveRef;
    private NativeCurve curveNat;
    private NativeArray<float> floatArray;

    [TearDown]
    public void TearDown() {
        if (curveNat.IsCreated) {
            curveNat.Dispose();
        }

        if (floatArray.IsCreated) {
            floatArray.Dispose();
        }
    }

    [Test]
    public void TestBasicCurveIsEqual([Values(WrapMode.Clamp, WrapMode.Loop, WrapMode.PingPong)] WrapMode preMode,
                                      [Values(WrapMode.Clamp, WrapMode.Loop, WrapMode.PingPong)] WrapMode postMode) {
        curveRef = new AnimationCurve(new Keyframe(0, 0), new Keyframe(1, 1));
        curveRef.preWrapMode = preMode;
        curveRef.postWrapMode = postMode;

        curveNat = new NativeCurve(curveRef);

        AssertCurvesEqual();
    }

    [Test]
    public void TestInfiniteTangentCurvesAreEqual([Values(WrapMode.Clamp, WrapMode.Loop, WrapMode.PingPong)] WrapMode preMode,
                                                  [Values(WrapMode.Clamp, WrapMode.Loop, WrapMode.PingPong)] WrapMode postMode) {
        curveRef = new AnimationCurve(new Keyframe(0, 0),
                                      new Keyframe(0.5f, 0.2f),
                                      new Keyframe(0.5f, 0.8f),
                                      new Keyframe(1, 1));
        curveRef.preWrapMode = preMode;
        curveRef.postWrapMode = postMode;

        curveNat = new NativeCurve(curveRef);

        AssertCurvesEqual();
    }

    [Test]
    public void TestMultiStackedKeyframesAreEqual([Values(WrapMode.Clamp, WrapMode.Loop, WrapMode.PingPong)] WrapMode preMode,
                                                  [Values(WrapMode.Clamp, WrapMode.Loop, WrapMode.PingPong)] WrapMode postMode) {
        curveRef = new AnimationCurve(new Keyframe(0, 0),
                                      new Keyframe(0.5f, 0.2f),
                                      new Keyframe(0.5f, 0.4f),
                                      new Keyframe(0.5f, 0.6f),
                                      new Keyframe(0.5f, 0.8f),
                                      new Keyframe(1, 1));
        curveRef.preWrapMode = preMode;
        curveRef.postWrapMode = postMode;

        curveNat = new NativeCurve(curveRef);

        AssertCurvesEqual();
    }

    [Test]
    [Repeat(10)]
    public void TestMultiCurveIsEqual([Values(WrapMode.Clamp, WrapMode.Loop, WrapMode.PingPong)] WrapMode preMode,
                              [Values(WrapMode.Clamp, WrapMode.Loop, WrapMode.PingPong)] WrapMode postMode) {
        Keyframe[] keyframes = new Keyframe[10];
        for (int i = 0; i < keyframes.Length; i++) {
            keyframes[i] = new Keyframe(i / 10.0f, Random.value);
        }
        curveRef = new AnimationCurve(keyframes);
        curveRef.preWrapMode = preMode;
        curveRef.postWrapMode = postMode;

        curveNat = new NativeCurve(curveRef);

        AssertCurvesEqual();
    }

    [Test]
    [Repeat(10)]
    public void TestUnlinkedTangentCurvesAreEqual([Values(WrapMode.Clamp, WrapMode.Loop, WrapMode.PingPong)] WrapMode preMode,
                                                  [Values(WrapMode.Clamp, WrapMode.Loop, WrapMode.PingPong)] WrapMode postMode) {
        Keyframe[] keyframes = new Keyframe[10];
        for (int i = 0; i < keyframes.Length; i++) {
            keyframes[i] = new Keyframe(i, Random.value, Random.Range(-10, 10), Random.Range(-10, 10));
        }
        curveRef = new AnimationCurve(keyframes);
        curveRef.preWrapMode = preMode;
        curveRef.postWrapMode = postMode;

        curveNat = new NativeCurve(curveRef);

        AssertCurvesEqual();
    }

    [Test]
    public void TestCanDeallocateOnJobCompletion([Values(Allocator.Persistent, Allocator.TempJob)] Allocator allocator) {
        curveRef = new AnimationCurve(new Keyframe(0, 0), new Keyframe(1, 1));
        var toTest = new NativeCurve(curveRef, allocator);

        var job = new JobThatDeallocates() {
            Curve = toTest
        }.Schedule();

        job.Complete();

        //We expect that trying to dispose again throws an exception
        Assert.That(() => toTest.Dispose(), Throws.InvalidOperationException);
    }

    [Test]
    public void TestCanReadFromMultipleJobThreads() {
        curveRef = new AnimationCurve(new Keyframe(0, 0), new Keyframe(1, 1));
        floatArray = new NativeArray<float>(512, Allocator.TempJob);

        new JobThatReadsOnMultiple() {
            Curve = curveRef.ToNative(),
            Results = floatArray
        }.Schedule(floatArray.Length, 8).Complete();

        for (int i = 0; i < floatArray.Length; i++) {
            Assert.That(floatArray[i], Is.EqualTo(curveRef.Evaluate(i / (float)floatArray.Length)).Within(TOLERANCE));
        }
    }

    private struct JobThatDeallocates : IJob {
        [DeallocateOnJobCompletion]
        public NativeCurve Curve;

        public void Execute() { }
    }

    private struct JobThatReadsOnMultiple : IJobParallelFor {
        [DeallocateOnJobCompletion]
        public NativeCurve Curve;
        public NativeArray<float> Results;

        public void Execute(int index) {
            Results[index] = Curve.Evaluate(index / (float)Results.Length);
        }
    }

    private void AssertCurvesEqual() {
        //Test for equality at each key
        for (int i = 0; i < curveRef.length; i++) {
            float t0 = curveRef[i].time;
            float t1 = t0 - 0.001f;
            float t2 = t0 + 0.001f;

            //Results should always be exact when sampling at the exact time as a keyframe
            AssertExactAt(t0);

            //Test the results on either side of each keyframe as well
            AssertAproxAt(t1);
            AssertAproxAt(t2);
        }

        //Test 100 different regions accross the curve
        for (int i = 0; i < 100; i++) {
            float t = Mathf.Lerp(curveRef[0].time, curveRef[curveRef.length - 1].time, i / 100.0f);
            AssertAproxAt(t);
        }

        //Test 100 different regions ranging from 0-100 durations away from the defined curve
        float duration = curveRef[curveRef.length - 1].time - curveRef[0].time;
        for (int i = 0; i < 100; i++) {
            float factor = i / 100.0f + i;
            float t0 = curveRef[0].time - factor * duration;
            float t1 = curveRef[curveRef.length - 1].time + factor * duration;

            AssertAproxAt(t0);
            AssertAproxAt(t1);
        }
    }

    private void AssertExactAt(float t) {
        float expected = curveRef.Evaluate(t);
        float actual = curveNat.Evaluate(t);
        Assert.That(actual, Is.EqualTo(expected), $"Expected value {expected} at time {t} but got {actual}.");
    }

    private void AssertAproxAt(float t) {
        float expected = curveRef.Evaluate(t);
        float actual = curveNat.Evaluate(t);
        Assert.That(actual, Is.EqualTo(expected).Within(TOLERANCE), $"Expected value {expected} at time {t} but got {actual}.");
    }
}
