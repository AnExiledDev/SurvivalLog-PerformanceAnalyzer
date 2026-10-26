using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using System.Threading;
using PerformanceAnalyzer.Core;

// Outside the Core namespace on purpose: other mods find this class by its full name, so the name is part of the contract.
namespace PerformanceAnalyzer
{
    /// <summary>
    /// The calls other mods make to time a section of their own code. They reach it through markers/PerfMarkers.cs, a
    /// file they copy into their mod, which finds this class by name and does nothing when the analyzer is not installed.
    /// </summary>
    public static class Markers
    {
        internal static readonly MarkerRegistry Registry = new();

        private static Func<int, int>? _begin;
        private static Action<int>? _end;

        /// <summary>A marker id for this section of <paramref name="owner"/>'s code. The same name from the same mod gives the same id.</summary>
        public static int Register(Assembly owner, string name) => Registry.Register(owner, name);

        /// <returns>A token for <see cref="End"/>, or a negative number when nothing is timed.</returns>
        public static int Begin(int marker)
        {
            var begin = _begin;

            if (begin == null) return Recorder.NotTimed;

            var probe = Registry.ProbeOf(marker);

            return probe < 0 ? Recorder.NotTimed : begin(probe);
        }

        public static void End(int token)
        {
            if (token >= 0) _end?.Invoke(token);
        }

        /// <summary>Points the markers at the recorder's probe calls.</summary>
        internal static void Attach(Func<int, int> begin, Action<int> end)
        {
            _end = end;
            _begin = begin;
        }
    }
}

namespace PerformanceAnalyzer.Core
{
    /// <summary>
    /// Sections other mods asked to have timed. A mod can register from any thread at any time; each marker becomes a
    /// probe the next time <see cref="Drain"/> runs on the main thread, and is not timed before that.
    /// </summary>
    public sealed class MarkerRegistry
    {
        public const int MaxNameLength = 80;

        private readonly object _lock = new();
        private readonly List<(Assembly Owner, string Name)> _markers = new();
        private readonly Dictionary<(Assembly, string), int> _ids = new();
        private int[] _probes = Array.Empty<int>();
        private int _drained;
        private int _pending;

        public int Count
        {
            get
            {
                lock (_lock) return _markers.Count;
            }
        }

        public int Register(Assembly owner, string name)
        {
            if (owner == null) throw new ArgumentNullException(nameof(owner));

            var clean = Clean(name);

            lock (_lock)
            {
                if (_ids.TryGetValue((owner, clean), out var id)) return id;

                id = _markers.Count;
                _markers.Add((owner, clean));
                _ids[(owner, clean)] = id;

                var probes = new int[id + 1];

                Array.Copy(_probes, probes, id);
                probes[id] = Recorder.NotTimed;
                Volatile.Write(ref _probes, probes);
                Volatile.Write(ref _pending, 1);

                return id;
            }
        }

        /// <summary>The recorder's probe for a marker, or <see cref="Recorder.NotTimed"/> until it has one.</summary>
        public int ProbeOf(int marker)
        {
            var probes = Volatile.Read(ref _probes);

            return (uint)marker < (uint)probes.Length ? probes[marker] : Recorder.NotTimed;
        }

        /// <summary>Gives every new marker a probe, charged to the mod that owns it. Main thread only, like every probe.</summary>
        /// <returns>How many markers got a probe.</returns>
        public int Drain(Recorder recorder, Func<Assembly, int> modOf)
        {
            if (Volatile.Read(ref _pending) == 0) return 0;

            lock (_lock)
            {
                var probes = (int[])_probes.Clone();
                var added = 0;

                for (; _drained < _markers.Count; _drained++, added++)
                {
                    var (owner, name) = _markers[_drained];

                    probes[_drained] = recorder.AddProbe(new ProbeInfo(name, ProbeKind.Marker, "", new[] { modOf(owner) }));
                }

                Volatile.Write(ref _probes, probes);
                Volatile.Write(ref _pending, 0);

                return added;
            }
        }

        /// <summary>Plain printable ASCII, so the report stays readable everywhere; anything else becomes '?'.</summary>
        public static string Clean(string? name)
        {
            var text = new StringBuilder();

            foreach (var c in (name ?? "").Trim())
            {
                if (text.Length == MaxNameLength) break;

                text.Append(c >= ' ' && c < 127 ? c : '?');
            }

            return text.Length == 0 ? "unnamed section" : text.ToString();
        }
    }
}
