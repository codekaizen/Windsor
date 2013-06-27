// Copyright 2004-2012 Castle Project - http://www.castleproject.org/
// 
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
// 
//     http://www.apache.org/licenses/LICENSE-2.0
// 
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

namespace Castle.MicroKernel.Releasers
{
	using System;
	using System.Collections.Generic;
	using System.Diagnostics;
	using System.Linq;
	using System.Security;
	using System.Threading;

	using Castle.Core;
	using Castle.Core.Internal;
	using Castle.Windsor.Diagnostics;

	/// <summary>
	///     Tracks all components requiring decommission (<see cref = "Burden.RequiresPolicyRelease" />)
	/// </summary>
	[Serializable]
	public class LifecycledComponentsReleasePolicy : IReleasePolicy
	{
#if !SILVERLIGHT
		private static int instanceId;
#endif
        private readonly Dictionary<InstanceEntry, Burden> instance2Burden =
            new Dictionary<InstanceEntry, Burden>();
        private Int64 currentTimestamp;
        
		private readonly Lock @lock = Lock.Create();
		private readonly ITrackedComponentsPerformanceCounter perfCounter;
		private ITrackedComponentsDiagnostic trackedComponentsDiagnostic;

		/// <param name = "kernel">
		///     Used to obtain <see cref = "ITrackedComponentsDiagnostic" /> if present.
		/// </param>
		public LifecycledComponentsReleasePolicy(IKernel kernel)
			: this(GetTrackedComponentsDiagnostic(kernel), null)
		{
		}

		/// <summary>
		///     Creates new policy which publishes its tracking components count to
		///     <paramref
		///         name = "trackedComponentsPerformanceCounter" />
		///     and exposes diagnostics into
		///     <paramref
		///         name = "trackedComponentsDiagnostic" />
		///     .
		/// </summary>
		/// <param name = "trackedComponentsDiagnostic"></param>
		/// <param name = "trackedComponentsPerformanceCounter"></param>
		public LifecycledComponentsReleasePolicy(ITrackedComponentsDiagnostic trackedComponentsDiagnostic,
		                                         ITrackedComponentsPerformanceCounter trackedComponentsPerformanceCounter)
		{
			this.trackedComponentsDiagnostic = trackedComponentsDiagnostic;
			perfCounter = trackedComponentsPerformanceCounter ?? NullPerformanceCounter.Instance;

			if (trackedComponentsDiagnostic != null)
			{
				trackedComponentsDiagnostic.TrackedInstancesRequested += trackedComponentsDiagnostic_TrackedInstancesRequested;
			}
		}

		private LifecycledComponentsReleasePolicy(LifecycledComponentsReleasePolicy parent)
			: this(parent.trackedComponentsDiagnostic, parent.perfCounter)
		{
		}

		private Burden[] TrackedObjects
		{
			get
			{
				using (var holder = @lock.ForReading(false))
				{
					if (holder.LockAcquired == false)
					{
						// TODO: that's sad... perhaps we should have waited...? But what do we do now? We're in the debugger. If some thread is keeping the lock
						// we could wait indefinitely. I guess the best way to proceed is to add a 200ms timeout to acquire the lock, and if not succeeded
						// assume that the other thread just waits and is not going anywhere and go ahead and read this anyway...
					}
					var array = instance2Burden.Values.ToArray();
					return array;
				}
			}
		}

		public void Dispose()
		{
			List<KeyValuePair<InstanceEntry, Burden>> burdens;
			using (@lock.ForWriting())
			{
				if (trackedComponentsDiagnostic != null)
				{
					trackedComponentsDiagnostic.TrackedInstancesRequested -= trackedComponentsDiagnostic_TrackedInstancesRequested;
					trackedComponentsDiagnostic = null;
				}
				burdens = instance2Burden.ToList();
				instance2Burden.Clear();
			}

            // descending by creation time
            burdens.Sort((a, b) => b.Key.CompareTo(a.Key));

			foreach (var burden in burdens)
			{
				burden.Value.Released -= OnInstanceReleased;
				perfCounter.DecrementTrackedInstancesCount();
				burden.Value.Release();
			}
		}

		public IReleasePolicy CreateSubPolicy()
		{
			var policy = new LifecycledComponentsReleasePolicy(this);
			return policy;
		}

		public bool HasTrack(object instance)
		{
			if (instance == null)
			{
				return false;
			}

			using (@lock.ForReading())
			{
                return instance2Burden.ContainsKey(new InstanceEntry(instance));
			}
		}

		public void Release(object instance)
		{
			if (instance == null)
			{
				return;
			}

			Burden burden;
			using (@lock.ForWriting())
			{
				// NOTE: we don't physically remove the instance from the instance2Burden collection here.
                // we do it in OnInstanceReleased event handler
                var entry = new InstanceEntry(instance);
				if (instance2Burden.TryGetValue(entry, out burden) == false)
				{
					return;
				}
			}
			burden.Release();
		}

		public virtual void Track(object instance, Burden burden)
		{
			if (burden.RequiresPolicyRelease == false)
			{
				var lifestyle = ((object)burden.Model.CustomLifestyle) ?? burden.Model.LifestyleType;
				throw new ArgumentException(
					string.Format(
						"Release policy was asked to track object '{0}', but its burden has 'RequiresPolicyRelease' set to false. If object is to be tracked the flag must be true. This is likely a bug in the lifetime manager '{1}'.",
						instance, lifestyle));
			}
			try
			{
				using (@lock.ForWriting())
                {
                    var timestamp = Interlocked.Increment(ref currentTimestamp);
                    instance2Burden.Add(new InstanceEntry(timestamp, instance), burden);
				}
			}
			catch (ArgumentNullException)
			{
				//eventually we should probably throw something more useful here too
				throw;
			}
			catch (ArgumentException)
			{
				throw HelpfulExceptionsUtil.TrackInstanceCalledMultipleTimes(instance, burden);
			}
			burden.Released += OnInstanceReleased;
			perfCounter.IncrementTrackedInstancesCount();
		}

		private void OnInstanceReleased(Burden burden)
		{
			using (@lock.ForWriting())
            {
                var entry = new InstanceEntry(burden.Instance);
				if (instance2Burden.Remove(entry) == false)
				{
					return;
				}
			}
			burden.Released -= OnInstanceReleased;
			perfCounter.DecrementTrackedInstancesCount();
		}

		private void trackedComponentsDiagnostic_TrackedInstancesRequested(object sender, TrackedInstancesEventArgs e)
		{
			e.AddRange(TrackedObjects);
		}

		/// <summary>
		///     Obtains <see cref = "ITrackedComponentsDiagnostic" /> from given <see cref = "IKernel" /> if present.
		/// </summary>
		/// <param name = "kernel"></param>
		/// <returns></returns>
		public static ITrackedComponentsDiagnostic GetTrackedComponentsDiagnostic(IKernel kernel)
		{
			var diagnosticsHost = (IDiagnosticsHost)kernel.GetSubSystem(SubSystemConstants.DiagnosticsKey);
			if (diagnosticsHost == null)
			{
				return null;
			}
			return diagnosticsHost.GetDiagnostic<ITrackedComponentsDiagnostic>();
		}

		/// <summary>
		///     Creates new <see cref = "ITrackedComponentsPerformanceCounter" /> from given <see cref = "IPerformanceMetricsFactory" />.
		/// </summary>
		/// <param name = "perfMetricsFactory"></param>
		/// <returns></returns>
#if !(SILVERLIGHT || DOTNET35 || CLIENTPROFILE)
		[SecuritySafeCritical]
#endif
		public static ITrackedComponentsPerformanceCounter GetTrackedComponentsPerformanceCounter(
			IPerformanceMetricsFactory perfMetricsFactory)
		{
#if SILVERLIGHT
			return NullPerformanceCounter.Instance;
#else
			var process = Process.GetCurrentProcess();
			var name = string.Format("Instance {0} | process {1} (id:{2})", Interlocked.Increment(ref instanceId),
			                         process.ProcessName, process.Id);
			return perfMetricsFactory.CreateInstancesTrackedByReleasePolicyCounter(name);
#endif
		}

        struct InstanceEntry : IEquatable<InstanceEntry>, IComparable<InstanceEntry>
        {
            public static readonly Int64 AnyTimestamp = 0;

            public InstanceEntry(Object instance)
            {
                Timestamp = AnyTimestamp;
                Instance = instance;
            }

            public InstanceEntry(Int64 timestamp, Object instance)
            {
                Timestamp = timestamp;
                Instance = instance;
            }

            public readonly Int64 Timestamp;
            public readonly Object Instance;

            /// <summary>
            /// Indicates whether the current object is equal to another object of the same type.
            /// </summary>
            /// <returns>
            /// true if the current object is equal to the <paramref name="other"/> parameter; otherwise, false.
            /// </returns>
            /// <param name="other">An object to compare with this object.</param>
            public Boolean Equals(InstanceEntry other)
            {
                return ReferenceEquals(Instance, other.Instance);
            }

            /// <summary>
            /// Indicates whether this instance and a specified object are equal.
            /// </summary>
            /// <returns>
            /// true if <paramref name="obj"/> and this instance are the same type and represent the same value; otherwise, false.
            /// </returns>
            /// <param name="obj">Another object to compare to. </param>
            public override Boolean Equals(object obj)
            {
                if (ReferenceEquals(null, obj))
                {
                    return false;
                }

                return obj is InstanceEntry && Equals((InstanceEntry)obj);
            }

            /// <summary>
            /// Returns the hash code for this instance.
            /// </summary>
            /// <returns>
            /// A 32-bit signed integer that is the hash code for this instance.
            /// </returns>
            public override Int32 GetHashCode()
            {
                return ReferenceEqualityComparer<Object>.Instance.GetHashCode(Instance);
            }

            public Int32 CompareTo(InstanceEntry other)
            {
                return Timestamp.CompareTo(other.Timestamp);
            }
        }
	}
}