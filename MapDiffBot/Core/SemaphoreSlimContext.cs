﻿using System;
using System.Threading;
using System.Threading.Tasks;

namespace MapDiffBot.Core
{
	/// <summary>
	/// Async lock context helper
	/// </summary>
    public sealed class SemaphoreSlimContext : IDisposable
    {
		/// <summary>
		/// Asyncronously locks a <paramref name="semaphore"/>
		/// </summary>
		/// <param name="semaphore">The <see cref="SemaphoreSlim"/> to lock</param>
		/// <param name="cancellationToken">The <see cref="CancellationToken"/> for the operation</param>
		/// <returns>A <see cref="Task{TResult}"/> resulting in the <see cref="SemaphoreSlimContext"/> for the lock</returns>
		public static async Task<SemaphoreSlimContext> Lock(SemaphoreSlim semaphore, CancellationToken cancellationToken)
		{
			if (semaphore == null)
				throw new ArgumentNullException(nameof(semaphore));
			await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
			cancellationToken.ThrowIfCancellationRequested();
			return new SemaphoreSlimContext(semaphore);
		}

		/// <summary>
		/// The locked <see cref="SemaphoreSlim"/>
		/// </summary>
		readonly SemaphoreSlim lockedSemaphore;

		/// <summary>
		/// If <see cref="Dispose"/> has been called
		/// </summary>
		bool disposed;

		/// <summary>
		/// Construct a <see cref="SemaphoreSlimContext"/>
		/// </summary>
		/// <param name="lockedSemaphore">The value of <see cref="lockedSemaphore"/></param>
		SemaphoreSlimContext(SemaphoreSlim lockedSemaphore) => this.lockedSemaphore = lockedSemaphore;

		/// <summary>
		/// Finalize the <see cref="SemaphoreSlimContext"/>
		/// </summary>
		~SemaphoreSlimContext() => Dispose();

		/// <summary>
		/// Release the lock on <see cref="lockedSemaphore"/>
		/// </summary>
		public void Dispose()
		{
			if (disposed)
				return;
			lockedSemaphore.Release();
			GC.SuppressFinalize(this);
			disposed = true;
		}
	}
}
