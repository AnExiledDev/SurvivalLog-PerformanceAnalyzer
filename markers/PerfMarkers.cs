// Performance Analyzer markers: time a section of your own mod's code.
//
// Copy this file into your mod as it is. It needs no reference to Performance Analyzer and does nothing when the
// analyzer is not installed, so your mod works the same for players without it.
//
//     private static readonly PerfMarker CropScan = new("Crop scan");
//
//     using (CropScan.Time())
//     {
//         // the code to time
//     }
//
// Each marker shows in the analyzer's Methods tab and report as its own row, kind "marker", charged to your mod, and
// in a stutter's list of what ran. Its time is taken out of the method around it, so you can see which part of a
// slow Update is the slow part. Name markers in plain English letters; other characters show as '?'.
//
// Make one marker per section and keep it in a static field: creating one is cheap, but a new name every call fills
// the analyzer's tables. A marker costs about the same as one analyzer probe when recording, and next to nothing
// otherwise. Work on another thread is counted but not timed.

#nullable enable

using System;
using System.Reflection;

namespace PerformanceMarkers
{
    internal sealed class PerfMarker
    {
        private const int NotRegistered = -2;

        private readonly string _name;
        private int _id = NotRegistered;

        public PerfMarker(string name)
        {
            _name = name;
        }

        /// <summary>Starts timing; the section ends when the returned scope is disposed.</summary>
        public Scope Time()
        {
            // Registered on first use, by when the analyzer has loaded whatever order the mods loaded in.
            if (_id == NotRegistered) _id = Api.Register(typeof(PerfMarker).Assembly, _name);

            return new Scope(_id < 0 ? -1 : Api.Begin(_id));
        }

        public readonly struct Scope : IDisposable
        {
            private readonly int _token;

            internal Scope(int token)
            {
                _token = token;
            }

            public void Dispose()
            {
                if (_token >= 0) Api.End(_token);
            }
        }

        private static class Api
        {
            private static Func<Assembly, string, int>? _register;
            private static Func<int, int>? _begin;
            private static Action<int>? _end;
            private static bool _looked;

            public static int Register(Assembly owner, string name)
            {
                Look();

                return _register == null ? -1 : _register(owner, name);
            }

            public static int Begin(int id) => _begin == null ? -1 : _begin(id);

            public static void End(int token) => _end?.Invoke(token);

            private static void Look()
            {
                if (_looked) return;

                _looked = true;

                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try
                    {
                        var type = assembly.GetType("PerformanceAnalyzer.Markers", false);

                        if (type == null) continue;

                        var register = type.GetMethod("Register", new[] { typeof(Assembly), typeof(string) });
                        var begin = type.GetMethod("Begin", new[] { typeof(int) });
                        var end = type.GetMethod("End", new[] { typeof(int) });

                        if (register == null || begin == null || end == null) continue;

                        _begin = (Func<int, int>)Delegate.CreateDelegate(typeof(Func<int, int>), begin);
                        _end = (Action<int>)Delegate.CreateDelegate(typeof(Action<int>), end);
                        _register = (Func<Assembly, string, int>)Delegate.CreateDelegate(typeof(Func<Assembly, string, int>), register);

                        return;
                    }
                    catch (Exception)
                    {
                        // An assembly that cannot be searched is not the analyzer.
                    }
                }
            }
        }
    }
}
