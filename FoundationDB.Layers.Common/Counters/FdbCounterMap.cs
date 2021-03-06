﻿#region BSD Licence
/* Copyright (c) 2013-2014, Doxense SAS
All rights reserved.

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are met:
	* Redistributions of source code must retain the above copyright
	  notice, this list of conditions and the following disclaimer.
	* Redistributions in binary form must reproduce the above copyright
	  notice, this list of conditions and the following disclaimer in the
	  documentation and/or other materials provided with the distribution.
	* Neither the name of Doxense nor the
	  names of its contributors may be used to endorse or promote products
	  derived from this software without specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND
ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
DISCLAIMED. IN NO EVENT SHALL <COPYRIGHT HOLDER> BE LIABLE FOR ANY
DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
(INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
(INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */
#endregion

namespace FoundationDB.Layers.Counters
{
	using FoundationDB.Client;
	using JetBrains.Annotations;
	using System;
	using System.Threading.Tasks;

	/// <summary>Providers a dictionary of 64-bit counters that can be updated atomically</summary>
	/// <typeparam name="TKey">Type of the key in the counter map</typeparam>
	public sealed class FdbCounterMap<TKey>
	{
		private static readonly Slice PlusOne = Slice.FromFixed64(1);
		private static readonly Slice MinusOne = Slice.FromFixed64(-1);

		/// <summary>Create a new counter map.</summary>
		public FdbCounterMap(FdbSubspace subspace)
			: this(subspace, KeyValueEncoders.Tuples.Key<TKey>())
		{ }

		/// <summary>Create a new counter map, using a specific key encoder.</summary>
		public FdbCounterMap(FdbSubspace subspace, IKeyEncoder<TKey> keyEncoder)
		{
			if (subspace == null) throw new ArgumentNullException("subspace");
			if (keyEncoder == null) throw new ArgumentNullException("keyEncoder");

			this.Subspace = subspace;
			this.KeyEncoder = keyEncoder;
			this.Location = new FdbEncoderSubspace<TKey>(subspace, keyEncoder);
		}

		/// <summary>Subspace used as a prefix for all items in this counter list</summary>
		public FdbSubspace Subspace { [NotNull] get; private set; }

		/// <summary>Encoder for the keys of the counter map</summary>
		public IKeyEncoder<TKey> KeyEncoder { [NotNull] get; private set; }

		internal FdbEncoderSubspace<TKey> Location { [NotNull] get; private set; }

		/// <summary>Add a value to a counter in one atomic operation</summary>
		/// <param name="transaction"></param>
		/// <param name="counterKey">Key of the counter, relative to the list's subspace</param>
		/// <param name="value">Value that will be added</param>
		/// <remarks>This operation will not cause the current transaction to conflict. It may create conflicts for transactions that would read the value of the counter.</remarks>
		public void Add([NotNull] IFdbTransaction transaction, [NotNull] TKey counterKey, long value)
		{
			if (transaction == null) throw new ArgumentNullException("transaction");
			if (counterKey == null) throw new ArgumentNullException("counterKey");

			//REVIEW: we could no-op if value == 0 but this may change conflict behaviour for other transactions...
			Slice param = value == 1 ? PlusOne : value == -1 ? MinusOne : Slice.FromFixed64(value);
			transaction.AtomicAdd(this.Location.EncodeKey(counterKey), param);
		}

		/// <summary>Subtract a value from a counter in one atomic operation</summary>
		/// <param name="transaction">Transaction to use for the operation</param>
		/// <param name="counterKey">Key of the counter, relative to the list's subspace</param>
		/// <param name="value">Value that will be substracted. If the value is zero</param>
		/// <remarks>This operation will not cause the current transaction to conflict. It may create conflicts for transactions that would read the value of the counter.</remarks>
		public void Subtract([NotNull] IFdbTransaction transaction, [NotNull] TKey counterKey, long value)
		{
			Add(transaction, counterKey, -value);
		}

		/// <summary>Increment the value of a counter in one atomic operation</summary>
		/// <param name="transaction">Transaction to use for the operation</param>
		/// <param name="counterKey">Key of the counter, relative to the list's subspace</param>
		/// <remarks>This operation will not cause the current transaction to conflict. It may create conflicts for transactions that would read the value of the counter.</remarks>
		public void Increment([NotNull] IFdbTransaction transaction, [NotNull] TKey counterKey)
		{
			Add(transaction, counterKey, 1);
		}

		/// <summary>Decrement the value of a counter in one atomic operation</summary>
		/// <param name="transaction">Transaction to use for the operation</param>
		/// <param name="counterKey">Key of the counter, relative to the list's subspace</param>
		/// <remarks>This operation will not cause the current transaction to conflict. It may create conflicts for transactions that would read the value of the counter.</remarks>
		public void Decrement([NotNull] IFdbTransaction transaction, [NotNull] TKey counterKey)
		{
			Add(transaction, counterKey, -1);
		}

		/// <summary>Read the value of a counter</summary>
		/// <param name="transaction">Transaction to use for the operation</param>
		/// <param name="counterKey">Key of the counter, relative to the list's subspace</param>
		/// <returns></returns>
		public async Task<long?> ReadAsync([NotNull] IFdbReadOnlyTransaction transaction, [NotNull] TKey counterKey)
		{
			if (transaction == null) throw new ArgumentNullException("transaction");
			if (counterKey == null) throw new ArgumentNullException("counterKey");

			var data = await transaction.GetAsync(this.Location.EncodeKey(counterKey)).ConfigureAwait(false);
			if (data.IsNullOrEmpty) return default(long?);
			return data.ToInt64();
		}

		/// <summary>Adds a value to a counter, and return its new value.</summary>
		/// <param name="transaction">Transaction to use for the operation</param>
		/// <param name="counterKey">Key of the counter, relative to the list's subspace</param>
		/// <returns>New value of the counter. Returns <paramref name="value"/> if the counter did not exist previously.</returns>
		/// <remarks>This method WILL conflict with other transactions!</remarks>
		public async Task<long> AddThenReadAsync([NotNull] IFdbTransaction transaction, [NotNull] TKey counterKey, long value)
		{
			if (transaction == null) throw new ArgumentNullException("transaction");
			if (counterKey == null) throw new ArgumentNullException("counterKey");

			var key = this.Location.EncodeKey(counterKey);
			var res = await transaction.GetAsync(key).ConfigureAwait(false);

			if (!res.IsNullOrEmpty) value += res.ToInt64();
			transaction.Set(key, Slice.FromFixed64(value));

			return value;
		}

		public Task<long> SubtractThenReadAsync([NotNull] IFdbTransaction transaction, [NotNull] TKey counterKey, long value)
		{
			return AddThenReadAsync(transaction, counterKey, -value);
		}

		public Task<long> IncrementThenReadAsync([NotNull] IFdbTransaction transaction, [NotNull] TKey counterKey)
		{
			return AddThenReadAsync(transaction, counterKey, 1);
		}

		public Task<long> DecrementThenReadAsync([NotNull] IFdbTransaction transaction, [NotNull] TKey counterKey)
		{
			return AddThenReadAsync(transaction, counterKey, -1);
		}

		/// <summary>Adds a value to a counter, but return its previous value.</summary>
		/// <param name="transaction">Transaction to use for the operation</param>
		/// <param name="counterKey">Key of the counter, relative to the list's subspace</param>
		/// <returns>Previous value of the counter. Returns 0 if the counter did not exist previously.</returns>
		/// <remarks>This method WILL conflict with other transactions!</remarks>
		public async Task<long> ReadThenAddAsync([NotNull] IFdbTransaction transaction, [NotNull] TKey counterKey, long value)
		{
			if (transaction == null) throw new ArgumentNullException("transaction");
			if (counterKey == null) throw new ArgumentNullException("counterKey");

			var key = this.Location.EncodeKey(counterKey);
			var res = await transaction.GetAsync(key).ConfigureAwait(false);

			long previous = res.IsNullOrEmpty ? 0 : res.ToInt64();
			transaction.Set(key, Slice.FromFixed64(value + previous));

			return previous;
		}

		public Task<long> ReadThenSubtractAsync([NotNull] IFdbTransaction transaction, [NotNull] TKey counterKey, long value)
		{
			return ReadThenAddAsync(transaction, counterKey, -value);
		}

		public Task<long> ReadThenIncrementAsync([NotNull] IFdbTransaction transaction, [NotNull] TKey counterKey)
		{
			return ReadThenAddAsync(transaction, counterKey, 1);
		}

		public Task<long> ReadThenDecrementAsync([NotNull] IFdbTransaction transaction, [NotNull] TKey counterKey)
		{
			return ReadThenAddAsync(transaction, counterKey, -1);
		}

	}

}
