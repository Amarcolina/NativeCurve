using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Assertions;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using static Unity.Collections.LowLevel.Unsafe.UnsafeUtility;

namespace Unity.Collections {

    public static class NativeCurveExtensions {

        /// <summary>
        /// Constructs a new NativeCurve from the given AnimationCurve.  Note that the returned curve still
        /// must be manually disposed.
        /// </summary>
        public static NativeCurve ToNative(this AnimationCurve curve, Allocator allocator = Allocator.TempJob) {
            return new NativeCurve(curve, allocator);
        }
    }

    /// <summary>
    /// NativeCurve is a simple re-implementation of AnimationCurve in a Job/Burst friendly way.  This container
    /// can be constructed directly from an AnimationCurve, or by using an array of keyframes.  Once created this
    /// container cannot be modified, which makes it very safe to use from multiple threads at once.
    /// 
    /// This container must be manually disposed of when you are finished with it, by calling Dispose. Alternatively 
    /// you can also attribute it with [DeallocateOnJobCompletion] and it will be automatically disposed of when that
    /// job is finished.
    /// 
    /// NOTE that this curve representation does not currently support custom curve weights!
    /// </summary>
    [NativeContainer]
    [NativeContainerIsReadOnly]
    [NativeContainerSupportsDeallocateOnJobCompletion]
    [StructLayout(LayoutKind.Sequential)]
    public struct NativeCurve : IDisposable {

        #region API

        /// <summary>
        /// Returns whether or not this NativeCurve has been created or not.  Once disposed
        /// this method will return false.
        /// </summary>
        public bool IsCreated => m_Buffer != IntPtr.Zero;

        /// <summary>
        /// Returns the number of keyframes stored in this curve container.
        /// </summary>
        public int Count => _count;

        /// <summary>
        /// Returns the start time of this curve in seconds.
        /// </summary>
        public float StartTime {
            get {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                AtomicSafetyHandle.CheckReadAndThrow(m_Safety);
#endif

                return _startTime;
            }
        }

        /// <summary>
        /// Returns the end time of this curve in seconds.
        /// </summary>
        public float EndTime {
            get {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                AtomicSafetyHandle.CheckReadAndThrow(m_Safety);
#endif

                return _endTime;
            }
        }

        /// <summary>
        /// Returns the duration of this curve in seconds.
        /// </summary>
        public float Duration {
            get {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                AtomicSafetyHandle.CheckReadAndThrow(m_Safety);
#endif

                return _endTime - _startTime;
            }
        }

        /// <summary>
        /// Returns the KeyFrame located at the given index.
        /// </summary>
        public KeyFrame this[int index] {
            get {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                AtomicSafetyHandle.CheckReadAndThrow(m_Safety);
                if (index < 0 || index >= _count) {
                    throw new IndexOutOfRangeException();
                }
#endif
                unsafe {
                    return ReadArrayElement<KeyFrame>((void*)m_Buffer, index);
                }
            }
            private set {
                unsafe {
                    WriteArrayElement((void*)m_Buffer, index, value);
                }
            }
        }

        /// <summary>
        /// Constructs a new NativeCurve that is an exact copy of the input AnimationCurve.
        /// </summary>
        public NativeCurve(AnimationCurve curve, Allocator allocator = Allocator.Persistent)
            : this(curve.length, allocator, curve.preWrapMode, curve.postWrapMode) {
            for (int i = 0; i < _count; i++) {
                this[i] = curve[i];
            }
            Init();
        }

        /// <summary>
        /// Constructs a new NativeCurve given a list of Keyframes.  Can optionally specify the
        /// pre and post wrap modes.
        /// </summary>
        public NativeCurve(IList<Keyframe> keyframes, Allocator allocator = Allocator.Persistent, WrapMode preWrapMode = WrapMode.Clamp, WrapMode postWrapMode = WrapMode.Clamp)
            : this(keyframes.Count, allocator, preWrapMode, postWrapMode) {
            for (int i = 0; i < _count; i++) {
                this[i] = keyframes[i];
            }
            Init();
        }

        /// <summary>
        /// Constructs a new NativeCurve given a list of KeyFrames.  Can optionally specify the
        /// pre and post wrap modes.
        /// </summary>
        public NativeCurve(IList<KeyFrame> keyframes, Allocator allocator = Allocator.Persistent, WrapMode preWrapMode = WrapMode.Clamp, WrapMode postWrapMode = WrapMode.Clamp)
            : this(keyframes.Count, allocator, preWrapMode, postWrapMode) {
            for (int i = 0; i < _count; i++) {
                this[i] = keyframes[i];
            }
            Init();
        }

        /// <summary>
        /// Constructs a new NativeCurve given a native slice of KeyFrames.  Can optionally specify the
        /// pre and post wrap modes.
        /// </summary>
        public NativeCurve(NativeSlice<KeyFrame> keyframes, Allocator allocator = Allocator.Persistent, WrapMode preWrapMode = WrapMode.Clamp, WrapMode postWrapMode = WrapMode.Clamp)
            : this(keyframes.Length, allocator, preWrapMode, postWrapMode) {
            for (int i = 0; i < _count; i++) {
                this[i] = keyframes[i];
            }
            Init();
        }

        /// <summary>
        /// Disposed of this container.  Once disposed, it cannot be used.
        /// </summary>
        public void Dispose() {

#if ENABLE_UNITY_COLLECTIONS_CHECKS
            DisposeSentinel.Dispose(ref m_Safety, ref m_DisposeSentinel);
#endif
            _count = 0;

            if (m_Buffer != IntPtr.Zero) {
                unsafe {
                    Free((void*)m_Buffer, m_AllocatorLabel);
                    m_Buffer = IntPtr.Zero;
                }
            }
        }

        /// <summary>
        /// Evaluates the curve at the given time.
        /// </summary>
        public float Evaluate(float t) {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            AtomicSafetyHandle.CheckReadAndThrow(m_Safety);
#endif
            if (_count == 1) {
                return this[0].Value;
            }

            if (t < _startTime) {
                AdjustTimeWithMode(ref t, _preWrapMode);
            } else if (t >= _endTime) {
                AdjustTimeWithMode(ref t, _postWrapMode);
            }

            int lowerBound = 0;
            int upperBound = _count - 1;

            while (true) {
                //Break out once we have closed the bound
                if (upperBound == lowerBound + 1) {
                    break;
                }

                int midpoint = lowerBound + (upperBound - lowerBound) / 2;

#if ENABLE_UNITY_COLLECTIONS_CHECKS
                Assert.AreNotEqual(lowerBound, midpoint);
                Assert.AreNotEqual(upperBound, midpoint);
#endif

                if (this[midpoint].Time > t) {
                    upperBound = midpoint;
                } else {
                    lowerBound = midpoint;
                }
            }

            return EvalCurved(this[lowerBound], this[upperBound], t);
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct KeyFrame {
            public float Time;
            public float Value;
            public float InTangent, OutTangent;

            public static implicit operator KeyFrame(Keyframe keyframe) {
                return new KeyFrame() {
                    Time = keyframe.time,
                    Value = keyframe.value,
                    InTangent = keyframe.inTangent,
                    OutTangent = keyframe.outTangent
                };
            }
        }

        #endregion

        #region IMPLEMENTATION

        private int _count;
        private float _startTime;
        private float _endTime;

        private WrapMode _preWrapMode, _postWrapMode;

        [NativeDisableUnsafePtrRestriction]
        private IntPtr m_Buffer;
        private Allocator m_AllocatorLabel;

#if ENABLE_UNITY_COLLECTIONS_CHECKS
        internal AtomicSafetyHandle m_Safety;

        [NativeSetClassTypeToNullOnSchedule]
        DisposeSentinel m_DisposeSentinel;
#endif

        private NativeCurve(int keyframes, Allocator allocator, WrapMode preWrapMode, WrapMode postWrapMode) {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            if (keyframes <= 0) {
                throw new ArgumentException("The number of keyframes must be greater than zero.");
            }

            switch (allocator) {
                case Allocator.Persistent:
                case Allocator.Temp:
                case Allocator.TempJob:
                    break;
                default:
                    throw new ArgumentException($"Expected an allocator type of Persistent, Temp, or TempJob, but got {allocator}.");
            }

            DisposeSentinel.Create(out m_Safety, out m_DisposeSentinel, 1, allocator);
#endif

            _count = keyframes;
            _preWrapMode = preWrapMode;
            _postWrapMode = postWrapMode;

            _startTime = 0;
            _endTime = 0;
            unsafe {
                m_Buffer = (IntPtr)Malloc(SizeOf<KeyFrame>() * _count, AlignOf<KeyFrame>(), allocator);
                m_AllocatorLabel = allocator;
            }
        }

        private void Init() {
            _startTime = this[0].Time;
            _endTime = this[_count - 1].Time;

#if ENABLE_UNITY_COLLECTIONS_CHECKS
            for (int i = 0; i < _count; i++) {
                KeyFrame kf = this[i];
                if (float.IsNaN(kf.Value) || float.IsInfinity(kf.Value)) {
                    throw new ArgumentException($"KeyFrame {i} had a value of {kf.Value}, which was expected to be finite.");
                }
                if (float.IsNaN(kf.Time) || float.IsInfinity(kf.Time)) {
                    throw new ArgumentException($"KeyFrame {i} had a time of {kf.Time}, which was expected to be finite.");
                }
                if (float.IsNaN(kf.InTangent) || float.IsNaN(kf.OutTangent)) {
                    throw new ArgumentException($"KeyFrame {i} had a tangent that was NaN.");
                }
            }

            if (_count >= 2) {
                if (this[0].Time == this[1].Time) {
                    throw new ArgumentException("The first two keyframes of a NativeCurve cannot have the same time.");
                }

                if (this[_count - 1].Time == this[_count - 2].Time) {
                    throw new ArgumentException("The last two keyframes of a NativeCurve cannot have the same time.");
                }
            }

            for (int i = 1; i < _count; i++) {
                float t0 = this[i - 1].Time;
                float t1 = this[i].Time;
                if (t0 > t1) {
                    throw new ArgumentException($"Keyframe {i - 1} at time {t0} should come after Keyframe {i} at time {t1}.");
                }
            }
#endif
        }

        private void AdjustTimeWithMode(ref float t, WrapMode mode) {
            switch (mode) {
                case WrapMode.Loop:
                    t = _startTime + Mathf.Repeat(t - _startTime, _endTime - _startTime);
                    break;
                case WrapMode.PingPong:
                    t = _startTime + Mathf.PingPong(t - _startTime, _endTime - _startTime);
                    break;
                default:
                    t = Mathf.Clamp(t, _startTime, _endTime);
                    break;
            }
        }

        private float EvalCurved(KeyFrame keyframe0, KeyFrame keyframe1, float time) {
            float dt = keyframe1.Time - keyframe0.Time;
            float t = (time - keyframe0.Time) / dt;

            float m0 = keyframe0.OutTangent * dt;
            float m1 = keyframe1.InTangent * dt;

            if (float.IsInfinity(m0) || float.IsInfinity(m1)) {
                return keyframe0.Value;
            }

            float t2 = t * t;
            float t3 = t2 * t;

            float a = 2 * t3 - 3 * t2 + 1;
            float b = t3 - 2 * t2 + t;
            float c = t3 - t2;
            float d = -2 * t3 + 3 * t2;

            return a * keyframe0.Value + b * m0 + c * m1 + d * keyframe1.Value;
        }

        #endregion
    }
}
