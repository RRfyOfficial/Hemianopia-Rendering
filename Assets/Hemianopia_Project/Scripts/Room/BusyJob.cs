using System;
using System.Collections.Generic;
using UnityEngine;

namespace Hemianopia.Room
{
    /// <summary>
    /// A small job that keeps the eyes busy: "find the shape that is pulsing and look at it".
    ///
    /// Why it exists: in the first tests the tester knew a surprise was coming on the blind side, so he swept
    /// that way straight away. That is hunting, and nobody does it in real life. With a job to do, the eyes
    /// move the way they would while someone is busy with something else, and a surprise is only found when
    /// the person's normal looking around happens to reach it (or when a nudge sends them there).
    ///
    /// How it works: one of the room's shapes starts pulsing. It can be any of them, including one on the
    /// blind side, so sometimes the person has to go and search for it. When they look at it, it stops, and
    /// after a short pause another one starts. If one is not found within giveUpSeconds, it moves on.
    ///
    /// It reports what it does through the Marked event (job_target, job_hit, job_skip), so the recorder
    /// knows what the person was busy with at each moment.
    /// </summary>
    public class BusyJob : MonoBehaviour
    {
        [Tooltip("Pause between one pulsing shape being found and the next one starting, in seconds (random in this range).")]
        public Vector2 gapRangeSeconds = new Vector2(0.2f, 0.6f);
        [Tooltip("Move on if the pulsing shape has not been looked at after this long, in seconds.")]
        public float giveUpSeconds = 6f;
        [Tooltip("The gaze must stay on the pulsing shape this long for it to count, in seconds. " +
                 "Stops a quick hop across the shape from counting as looking at it.")]
        public float holdSeconds = 0.35f;

        public bool IsRunning { get; private set; }
        /// <summary>How many pulsing shapes were looked at since Begin.</summary>
        public int Hits { get; private set; }
        /// <summary>How many shapes have pulsed since Begin.</summary>
        public int Targets { get; private set; }

        /// <summary>Fires with a short label each time something happens (for the recorder).</summary>
        public event Action<string> Marked;

        readonly List<TrackedObject> _pool = new List<TrackedObject>();
        TrackedObject _target;
        TrackedObject _last;
        double _nextAt;
        double _targetSince;
        float _held; // how long the gaze has stayed on the pulsing shape without a break

        public void Begin(List<TrackedObject> shapes)
        {
            End();
            _pool.Clear();
            if (shapes != null)
            {
                for (int i = 0; i < shapes.Count; i++)
                    if (shapes[i] != null) _pool.Add(shapes[i]);
            }
            if (_pool.Count == 0) return;

            Hits = 0;
            Targets = 0;
            _last = null;
            _target = null;
            _nextAt = Time.realtimeSinceStartupAsDouble + 0.5;
            IsRunning = true;
        }

        public void End()
        {
            if (_target != null) _target.Pulsing = false;
            _target = null;
            IsRunning = false;
        }

        void OnDisable()
        {
            End();
        }

        void Update()
        {
            if (!IsRunning) return;
            double now = Time.realtimeSinceStartupAsDouble;

            if (_target == null)
            {
                if (now >= _nextAt) PickNext(now);
                return;
            }

            // Count it only if the gaze settles on the shape. A hop across it resets the timer.
            _held = _target.LookedAtNow ? _held + Time.unscaledDeltaTime : 0f;

            if (_held >= holdSeconds)
            {
                Hits++;
                Marked?.Invoke("job_hit:" + _target.label);
                Release(now);
            }
            else if (now - _targetSince > giveUpSeconds)
            {
                Marked?.Invoke("job_skip:" + _target.label);
                Release(now);
            }
        }

        void PickNext(double now)
        {
            _pool.RemoveAll(o => o == null); // shapes can be destroyed when the room is hidden
            if (_pool.Count == 0)
            {
                End();
                return;
            }

            // Any shape except the one that just pulsed, and not one the person is already looking at
            // (that would count as found straight away).
            TrackedObject pick = null;
            for (int attempt = 0; attempt < 12; attempt++)
            {
                TrackedObject candidate = _pool[UnityEngine.Random.Range(0, _pool.Count)];
                if ((candidate != _last && !candidate.LookedAtNow) || _pool.Count == 1)
                {
                    pick = candidate;
                    break;
                }
            }
            if (pick == null) pick = _pool[0];

            _target = pick;
            _held = 0f;
            _target.Pulsing = true;
            _targetSince = now;
            Targets++;
            Marked?.Invoke("job_target:" + _target.label);
        }

        void Release(double now)
        {
            if (_target != null) _target.Pulsing = false;
            _last = _target;
            _target = null;
            _nextAt = now + UnityEngine.Random.Range(gapRangeSeconds.x, gapRangeSeconds.y);
        }
    }
}
