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

        public static NativeCurve ToNative(this AnimationCurve curve, Allocator allocator = Allocator.TempJob) {
            return new NativeCurve(curve, allocator);
        }
    }

    /// <summary>
    /// NativeCurve is a simple re-implementation of AnimationCurve in a Job/Burst friendly way.
    /// </summary>
    [NativeContainer]
    [NativeContainerIsReadOnly]
    [NativeContainerSupportsDeallocateOnJobCompletion]
    [StructLayout(LayoutKind.Sequential)]
    public struct NativeCurve : IDisposable {

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

        public bool IsCreated => m_Buffer != IntPtr.Zero;

        public float StartTime {
            get {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                AtomicSafetyHandle.CheckReadAndThrow(m_Safety);
#endif

                return _startTime;
            }
        }

        public float EndTime {
            get {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                AtomicSafetyHandle.CheckReadAndThrow(m_Safety);
#endif

                return _endTime;
            }
        }

        public float Duration {
            get {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                AtomicSafetyHandle.CheckReadAndThrow(m_Safety);
#endif

                return _endTime - _startTime;
            }
        }

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
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                if (index < 0 || index >= _count) {
                    throw new IndexOutOfRangeException();
                }
#endif
                unsafe {
                    WriteArrayElement((void*)m_Buffer, index, value);
                }
            }
        }

        public NativeCurve(IList<Keyframe> keyframes, Allocator allocator = Allocator.Persistent, WrapMode preWrapMode = WrapMode.Clamp, WrapMode postWrapMode = WrapMode.Clamp)
            : this(keyframes.Count, allocator, preWrapMode, postWrapMode) {
            for (int i = 0; i < _count; i++) {
                this[i] = keyframes[i];
            }
            _endTime = this[_count - 1].Time;
        }

        public NativeCurve(IList<KeyFrame> keyframes, Allocator allocator = Allocator.Persistent, WrapMode preWrapMode = WrapMode.Clamp, WrapMode postWrapMode = WrapMode.Clamp)
            : this(keyframes.Count, allocator, preWrapMode, postWrapMode) {
            for (int i = 0; i < _count; i++) {
                this[i] = keyframes[i];
            }
            _startTime = this[0].Time;
            _endTime = this[_count - 1].Time;
        }

        public NativeCurve(NativeSlice<KeyFrame> keyframes, Allocator allocator = Allocator.Persistent, WrapMode preWrapMode = WrapMode.Clamp, WrapMode postWrapMode = WrapMode.Clamp)
            : this(keyframes.Length, allocator, preWrapMode, postWrapMode) {
            for (int i = 0; i < _count; i++) {
                this[i] = keyframes[i];
            }
            _startTime = this[0].Time;
            _endTime = this[_count - 1].Time;
        }

        public NativeCurve(AnimationCurve curve, Allocator allocator = Allocator.Persistent)
            : this(curve.length, allocator, curve.preWrapMode, curve.postWrapMode) {
            for (int i = 0; i < _count; i++) {
                this[i] = curve[i];
            }
            _startTime = this[0].Time;
            _endTime = this[_count - 1].Time;
        }

        public NativeCurve(int keyframes, Allocator allocator = Allocator.Persistent, WrapMode preWrapMode = WrapMode.Clamp, WrapMode postWrapMode = WrapMode.Clamp) {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            DisposeSentinel.Create(out m_Safety, out m_DisposeSentinel, 0, allocator);
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

        public void Dispose() {
            unsafe {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                DisposeSentinel.Dispose(ref m_Safety, ref m_DisposeSentinel);
#endif
                _count = 0;

                if (m_Buffer != IntPtr.Zero) {
                    Free((void*)m_Buffer, m_AllocatorLabel);
                    m_Buffer = IntPtr.Zero;
                }
            }
        }

        public float Evaluate(float t) {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            AtomicSafetyHandle.CheckReadAndThrow(m_Safety);
#endif

            if (t < _startTime) {
                AdjustTimeWithMode(ref t, _preWrapMode);
            } else if (t >= _endTime) {
                AdjustTimeWithMode(ref t, _postWrapMode);
            }

#if ENABLE_UNITY_COLLECTIONS_CHECKS
            Assert.IsTrue(t >= _startTime);
            Assert.IsTrue(t <= _endTime);
#endif

            for (int i = 1; i < _count; i++) {
                if (t < this[i].Time) {
                    return EvalCurve(this[i - 1], this[i], t);
                }
            }

            return this[_count - 1].Value;
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

        private float EvalCurve(KeyFrame keyframe0, KeyFrame keyframe1, float time) {
            float t = (time - keyframe0.Time) / (keyframe1.Time - keyframe0.Time);
            float dt = keyframe1.Time - keyframe0.Time;

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
    }
}
