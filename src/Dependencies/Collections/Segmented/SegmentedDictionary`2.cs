﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

#nullable enable

// NOTE: This code is derived from an implementation originally in dotnet/runtime:
// https://github.com/dotnet/runtime/blob/v8.0.3/src/libraries/System.Private.CoreLib/src/System/Collections/Generic/Dictionary.cs
//
// See the commentary in https://github.com/dotnet/roslyn/pull/50156 for notes on incorporating changes made to the
// reference implementation.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.CodeAnalysis.Collections.Internal;

namespace Microsoft.CodeAnalysis.Collections
{
    /// <summary>
    /// Represents a collection of keys and values.
    /// </summary>
    /// <remarks>
    /// <para>This collection has the same performance characteristics as <see cref="Dictionary{TKey, TValue}"/>, but
    /// uses segmented arrays to avoid allocations in the Large Object Heap.</para>
    /// </remarks>
    /// <typeparam name="TKey">The type of the keys in the dictionary.</typeparam>
    /// <typeparam name="TValue">The type of the values in the dictionary.</typeparam>
    [DebuggerTypeProxy(typeof(IDictionaryDebugView<,>))]
    [DebuggerDisplay("Count = {Count}")]
    internal sealed partial class SegmentedDictionary<TKey, TValue> : IDictionary<TKey, TValue>, IDictionary, IReadOnlyDictionary<TKey, TValue>
        where TKey : notnull
    {
        private const bool SupportsComparerDevirtualization
#if NET
            = true;
#else
            = false;
#endif

        private static IEnumerator<KeyValuePair<TKey, TValue>>? s_emptyEnumerator;

        private SegmentedArray<int> _buckets;
        private SegmentedArray<Entry> _entries;
        private ulong _fastModMultiplier;
        private int _count;
        private int _freeList;
        private int _freeCount;
        private int _version;
#if NET
        private readonly IEqualityComparer<TKey>? _comparer;
#else
        /// <summary>
        /// <see cref="EqualityComparer{T}.Default"/> doesn't devirtualize on .NET Framework, so we always ensure
        /// <see cref="_comparer"/> is initialized to a non-<see langword="null"/> value.
        /// </summary>
        private readonly IEqualityComparer<TKey> _comparer;
#endif
        private KeyCollection? _keys;
        private ValueCollection? _values;
        private const int StartOfFreeList = -3;

        public SegmentedDictionary()
            : this(0, null)
        {
        }

        public SegmentedDictionary(int capacity)
            : this(capacity, null)
        {
        }

        public SegmentedDictionary(IEqualityComparer<TKey>? comparer)
            : this(0, comparer)
        {
        }

        public SegmentedDictionary(int capacity, IEqualityComparer<TKey>? comparer)
        {
            if (capacity < 0)
            {
                ThrowHelper.ThrowArgumentOutOfRangeException(ExceptionArgument.capacity);
            }

            if (capacity > 0)
            {
                Initialize(capacity);
            }

            // For reference types, we always want to store a comparer instance, either
            // the one provided, or if one wasn't provided, the default (accessing
            // EqualityComparer<TKey>.Default with shared generics on every dictionary
            // access can add measurable overhead).  For value types, if no comparer is
            // provided, or if the default is provided, we'd prefer to use
            // EqualityComparer<TKey>.Default.Equals on every use, enabling the JIT to
            // devirtualize and possibly inline the operation.
            if (!typeof(TKey).IsValueType)
            {
                _comparer = comparer ?? EqualityComparer<TKey>.Default;
            }
            else if (comparer is not null && // first check for null to avoid forcing default comparer instantiation unnecessarily
                     comparer != EqualityComparer<TKey>.Default)
            {
                _comparer = comparer;
            }

#if !NETCOREAPP
            // .NET Framework doesn't support devirtualization, so we always initialize comparer to a non-null value
            _comparer ??= EqualityComparer<TKey>.Default;
#endif
        }

        public SegmentedDictionary(IDictionary<TKey, TValue> dictionary)
            : this(dictionary, null)
        {
        }

        public SegmentedDictionary(IDictionary<TKey, TValue> dictionary, IEqualityComparer<TKey>? comparer)
            : this(dictionary?.Count ?? 0, comparer)
        {
            if (dictionary == null)
            {
                ThrowHelper.ThrowArgumentNullException(ExceptionArgument.dictionary);
            }

            AddRange(dictionary);
        }

        public SegmentedDictionary(IEnumerable<KeyValuePair<TKey, TValue>> collection)
            : this(collection, null)
        {
        }

        public SegmentedDictionary(IEnumerable<KeyValuePair<TKey, TValue>> collection, IEqualityComparer<TKey>? comparer)
            : this((collection as ICollection<KeyValuePair<TKey, TValue>>)?.Count ?? 0, comparer)
        {
            if (collection == null)
            {
                ThrowHelper.ThrowArgumentNullException(ExceptionArgument.collection);
            }

            AddRange(collection);
        }

        private void AddRange(IEnumerable<KeyValuePair<TKey, TValue>> enumerable)
        {
            // It is likely that the passed-in enumerable is SegmentedDictionary<TKey,TValue>. When this is the case,
            // avoid the enumerator allocation and overhead by looping through the entries array directly.
            // We only do this when dictionary is SegmentedDictionary<TKey,TValue> and not a subclass, to maintain
            // back-compat with subclasses that may have overridden the enumerator behavior.
            if (enumerable.GetType() == typeof(SegmentedDictionary<TKey, TValue>))
            {
                var source = (SegmentedDictionary<TKey, TValue>)enumerable;

                if (source.Count == 0)
                {
                    // Nothing to copy, all done
                    return;
                }

                // This is not currently a true .AddRange as it needs to be an initialized dictionary
                // of the correct size, and also an empty dictionary with no current entities (and no argument checks).
                Debug.Assert(_entries.Length >= source.Count);
                Debug.Assert(_count == 0);

                var oldEntries = source._entries;
                if (source._comparer == _comparer)
                {
                    // If comparers are the same, we can copy _entries without rehashing.
                    CopyEntries(oldEntries, source._count);
                    return;
                }

                // Comparers differ need to rehash all the entries via Add
                var count = source._count;
                for (var i = 0; i < count; i++)
                {
                    // Only copy if an entry
                    if (oldEntries[i]._next >= -1)
                    {
                        Add(oldEntries[i]._key, oldEntries[i]._value);
                    }
                }
                return;
            }

            // We similarly special-case KVP<>[] and List<KVP<>>, as they're commonly used to seed dictionaries, and
            // we want to avoid the enumerator costs (e.g. allocation) for them as well. Extract a span if possible.
            ReadOnlySpan<KeyValuePair<TKey, TValue>> span;
            if (enumerable is KeyValuePair<TKey, TValue>[] array)
            {
                span = array;
            }
#if NET5_0_OR_GREATER
            else if (enumerable.GetType() == typeof(List<KeyValuePair<TKey, TValue>>))
            {
                span = CollectionsMarshal.AsSpan((List<KeyValuePair<TKey, TValue>>)enumerable);
            }
#endif
            else
            {
                // Fallback path for all other enumerables
                foreach (var pair in enumerable)
                {
                    Add(pair.Key, pair.Value);
                }
                return;
            }

            // We got a span. Add the elements to the dictionary.
            foreach (var pair in span)
            {
                Add(pair.Key, pair.Value);
            }
        }

        public IEqualityComparer<TKey> Comparer
        {
            get
            {
                return _comparer ?? EqualityComparer<TKey>.Default;
            }
        }

        public int Count => _count - _freeCount;

        public KeyCollection Keys => _keys ??= new KeyCollection(this);

        ICollection<TKey> IDictionary<TKey, TValue>.Keys => Keys;

        IEnumerable<TKey> IReadOnlyDictionary<TKey, TValue>.Keys => Keys;

        public ValueCollection Values => _values ??= new ValueCollection(this);

        ICollection<TValue> IDictionary<TKey, TValue>.Values => Values;

        IEnumerable<TValue> IReadOnlyDictionary<TKey, TValue>.Values => Values;

        public TValue this[TKey key]
        {
            get
            {
                ref var value = ref FindValue(key);
                if (!RoslynUnsafe.IsNullRef(ref value))
                {
                    return value;
                }

                ThrowHelper.ThrowKeyNotFoundException(key);
                return default;
            }
            set
            {
                var modified = TryInsert(key, value, InsertionBehavior.OverwriteExisting);
                Debug.Assert(modified);
            }
        }

        public void Add(TKey key, TValue value)
        {
            var modified = TryInsert(key, value, InsertionBehavior.ThrowOnExisting);
            Debug.Assert(modified); // If there was an existing key and the Add failed, an exception will already have been thrown.
        }

        void ICollection<KeyValuePair<TKey, TValue>>.Add(KeyValuePair<TKey, TValue> keyValuePair)
            => Add(keyValuePair.Key, keyValuePair.Value);

        bool ICollection<KeyValuePair<TKey, TValue>>.Contains(KeyValuePair<TKey, TValue> keyValuePair)
        {
            ref var value = ref FindValue(keyValuePair.Key);
            if (!RoslynUnsafe.IsNullRef(ref value) && EqualityComparer<TValue>.Default.Equals(value, keyValuePair.Value))
            {
                return true;
            }

            return false;
        }

        bool ICollection<KeyValuePair<TKey, TValue>>.Remove(KeyValuePair<TKey, TValue> keyValuePair)
        {
            ref var value = ref FindValue(keyValuePair.Key);
            if (!RoslynUnsafe.IsNullRef(ref value) && EqualityComparer<TValue>.Default.Equals(value, keyValuePair.Value))
            {
                Remove(keyValuePair.Key);
                return true;
            }

            return false;
        }

        public void Clear()
        {
            var count = _count;
            if (count > 0)
            {
                Debug.Assert(_buckets.Length > 0, "_buckets should be non-empty");
                Debug.Assert(_entries.Length > 0, "_entries should be non-empty");

                SegmentedArray.Clear(_buckets);

                _count = 0;
                _freeList = -1;
                _freeCount = 0;
                SegmentedArray.Clear(_entries, 0, count);
            }
        }

        public bool ContainsKey(TKey key)
            => !RoslynUnsafe.IsNullRef(ref FindValue(key));

        public bool ContainsValue(TValue value)
        {
            var entries = _entries;
            if (value == null)
            {
                for (var i = 0; i < _count; i++)
                {
                    if (entries[i]._next >= -1 && entries[i]._value == null)
                    {
                        return true;
                    }
                }
            }
            else if (SupportsComparerDevirtualization && typeof(TValue).IsValueType)
            {
                // ValueType: Devirtualize with EqualityComparer<TValue>.Default intrinsic
                for (var i = 0; i < _count; i++)
                {
                    if (entries[i]._next >= -1 && EqualityComparer<TValue>.Default.Equals(entries[i]._value, value))
                    {
                        return true;
                    }
                }
            }
            else
            {
                // Object type: Shared Generic, EqualityComparer<TValue>.Default won't devirtualize
                // https://github.com/dotnet/runtime/issues/10050
                // So cache in a local rather than get EqualityComparer per loop iteration
                var defaultComparer = EqualityComparer<TValue>.Default;
                for (var i = 0; i < _count; i++)
                {
                    if (entries[i]._next >= -1 && defaultComparer.Equals(entries[i]._value, value))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private void CopyTo(KeyValuePair<TKey, TValue>[] array, int index)
        {
            if (array == null)
            {
                ThrowHelper.ThrowArgumentNullException(ExceptionArgument.array);
            }

            if ((uint)index > (uint)array.Length)
            {
                ThrowHelper.ThrowIndexArgumentOutOfRange_NeedNonNegNumException();
            }

            if (array.Length - index < Count)
            {
                ThrowHelper.ThrowArgumentException(ExceptionResource.Arg_ArrayPlusOffTooSmall);
            }

            var count = _count;
            var entries = _entries;
            for (var i = 0; i < count; i++)
            {
                if (entries[i]._next >= -1)
                {
                    array[index++] = new KeyValuePair<TKey, TValue>(entries[i]._key, entries[i]._value);
                }
            }
        }

        public Enumerator GetEnumerator()
            => new(this, Enumerator.KeyValuePair);

        IEnumerator<KeyValuePair<TKey, TValue>> IEnumerable<KeyValuePair<TKey, TValue>>.GetEnumerator() =>
            Count == 0 ? GetEmptyEnumerator() :
            GetEnumerator();

        private static IEnumerator<KeyValuePair<TKey, TValue>> GetEmptyEnumerator()
        {
            return LazyInitializer.EnsureInitialized(ref s_emptyEnumerator, static () => new Enumerator(new SegmentedDictionary<TKey, TValue>(), Enumerator.KeyValuePair))!;
        }

        private ref TValue FindValue(TKey key)
        {
            if (key == null)
            {
                ThrowHelper.ThrowArgumentNullException(ExceptionArgument.key);
            }

            ref var entry = ref RoslynUnsafe.NullRef<Entry>();
            if (_buckets.Length > 0)
            {
                Debug.Assert(_entries.Length > 0, "expected entries to be non-empty");
                var comparer = _comparer;
                if (SupportsComparerDevirtualization
                    && typeof(TKey).IsValueType // comparer can only be null for value types; enable JIT to eliminate entire if block for ref types
                    && comparer == null)
                {
                    var hashCode = (uint)key.GetHashCode();
                    var i = GetBucket(hashCode);
                    var entries = _entries;
                    uint collisionCount = 0;

                    // ValueType: Devirtualize with EqualityComparer<TKey>.Default intrinsic
                    i--; // Value in _buckets is 1-based; subtract 1 from i. We do it here so it fuses with the following conditional.
                    do
                    {
                        // Should be a while loop https://github.com/dotnet/runtime/issues/9422
                        // Test in if to drop range check for following array access
                        if ((uint)i >= (uint)entries.Length)
                        {
                            goto ReturnNotFound;
                        }

                        entry = ref entries[i];
                        if (entry._hashCode == hashCode && EqualityComparer<TKey>.Default.Equals(entry._key, key))
                        {
                            goto ReturnFound;
                        }

                        i = entry._next;

                        collisionCount++;
                    } while (collisionCount <= (uint)entries.Length);

                    // The chain of entries forms a loop; which means a concurrent update has happened.
                    // Break out of the loop and throw, rather than looping forever.
                    goto ConcurrentOperation;
                }
                else
                {
                    Debug.Assert(comparer is not null);
                    var hashCode = (uint)comparer!.GetHashCode(key);
                    var i = GetBucket(hashCode);
                    var entries = _entries;
                    uint collisionCount = 0;
                    i--; // Value in _buckets is 1-based; subtract 1 from i. We do it here so it fuses with the following conditional.
                    do
                    {
                        // Should be a while loop https://github.com/dotnet/runtime/issues/9422
                        // Test in if to drop range check for following array access
                        if ((uint)i >= (uint)entries.Length)
                        {
                            goto ReturnNotFound;
                        }

                        entry = ref entries[i];
                        if (entry._hashCode == hashCode && comparer.Equals(entry._key, key))
                        {
                            goto ReturnFound;
                        }

                        i = entry._next;

                        collisionCount++;
                    } while (collisionCount <= (uint)entries.Length);

                    // The chain of entries forms a loop; which means a concurrent update has happened.
                    // Break out of the loop and throw, rather than looping forever.
                    goto ConcurrentOperation;
                }
            }

            goto ReturnNotFound;

ConcurrentOperation:
            ThrowHelper.ThrowInvalidOperationException_ConcurrentOperationsNotSupported();
ReturnFound:
            ref TValue value = ref entry._value;
Return:
            return ref value;
ReturnNotFound:
            value = ref RoslynUnsafe.NullRef<TValue>();
            goto Return;
        }

        private int Initialize(int capacity)
        {
            var size = HashHelpers.GetPrime(capacity);
            var buckets = new SegmentedArray<int>(size);
            var entries = new SegmentedArray<Entry>(size);

            // Assign member variables after both arrays allocated to guard against corruption from OOM if second fails
            _freeList = -1;
            _fastModMultiplier = HashHelpers.GetFastModMultiplier((uint)size);
            _buckets = buckets;
            _entries = entries;

            return size;
        }

        private bool TryInsert(TKey key, TValue value, InsertionBehavior behavior)
        {
            if (key == null)
            {
                ThrowHelper.ThrowArgumentNullException(ExceptionArgument.key);
            }

            if (_buckets.Length == 0)
            {
                Initialize(0);
            }
            Debug.Assert(_buckets.Length > 0);

            var entries = _entries;
            Debug.Assert(entries.Length > 0, "expected entries to be non-empty");

            var comparer = _comparer;
            Debug.Assert(comparer is not null || (SupportsComparerDevirtualization && typeof(TKey).IsValueType));
            var hashCode = (uint)((SupportsComparerDevirtualization && typeof(TKey).IsValueType && comparer == null) ? key.GetHashCode() : comparer!.GetHashCode(key));

            uint collisionCount = 0;
            ref var bucket = ref GetBucket(hashCode);
            var i = bucket - 1; // Value in _buckets is 1-based

            if (SupportsComparerDevirtualization
                && typeof(TKey).IsValueType // comparer can only be null for value types; enable JIT to eliminate entire if block for ref types
                && comparer == null)
            {
                // ValueType: Devirtualize with EqualityComparer<TKey>.Default intrinsic
                while (true)
                {
                    // Should be a while loop https://github.com/dotnet/runtime/issues/9422
                    // Test uint in if rather than loop condition to drop range check for following array access
                    if ((uint)i >= (uint)entries.Length)
                    {
                        break;
                    }

                    if (entries[i]._hashCode == hashCode && EqualityComparer<TKey>.Default.Equals(entries[i]._key, key))
                    {
                        if (behavior == InsertionBehavior.OverwriteExisting)
                        {
                            entries[i]._value = value;
                            return true;
                        }

                        if (behavior == InsertionBehavior.ThrowOnExisting)
                        {
                            ThrowHelper.ThrowAddingDuplicateWithKeyArgumentException(key);
                        }

                        return false;
                    }

                    i = entries[i]._next;

                    collisionCount++;
                    if (collisionCount > (uint)entries.Length)
                    {
                        // The chain of entries forms a loop; which means a concurrent update has happened.
                        // Break out of the loop and throw, rather than looping forever.
                        ThrowHelper.ThrowInvalidOperationException_ConcurrentOperationsNotSupported();
                    }
                }
            }
            else
            {
                Debug.Assert(comparer is not null);
                while (true)
                {
                    // Should be a while loop https://github.com/dotnet/runtime/issues/9422
                    // Test uint in if rather than loop condition to drop range check for following array access
                    if ((uint)i >= (uint)entries.Length)
                    {
                        break;
                    }

                    if (entries[i]._hashCode == hashCode && comparer!.Equals(entries[i]._key, key))
                    {
                        if (behavior == InsertionBehavior.OverwriteExisting)
                        {
                            entries[i]._value = value;
                            return true;
                        }

                        if (behavior == InsertionBehavior.ThrowOnExisting)
                        {
                            ThrowHelper.ThrowAddingDuplicateWithKeyArgumentException(key);
                        }

                        return false;
                    }

                    i = entries[i]._next;

                    collisionCount++;
                    if (collisionCount > (uint)entries.Length)
                    {
                        // The chain of entries forms a loop; which means a concurrent update has happened.
                        // Break out of the loop and throw, rather than looping forever.
                        ThrowHelper.ThrowInvalidOperationException_ConcurrentOperationsNotSupported();
                    }
                }
            }

            int index;
            if (_freeCount > 0)
            {
                index = _freeList;
                Debug.Assert((StartOfFreeList - entries[_freeList]._next) >= -1, "shouldn't overflow because `next` cannot underflow");
                _freeList = StartOfFreeList - entries[_freeList]._next;
                _freeCount--;
            }
            else
            {
                var count = _count;
                if (count == entries.Length)
                {
                    Resize();
                    bucket = ref GetBucket(hashCode);
                }
                index = count;
                _count = count + 1;
                entries = _entries;
            }

            ref var entry = ref entries![index];
            entry._hashCode = hashCode;
            entry._next = bucket - 1; // Value in _buckets is 1-based
            entry._key = key;
            entry._value = value;
            bucket = index + 1; // Value in _buckets is 1-based
            _version++;
            return true;
        }

        private void Resize()
            => Resize(HashHelpers.ExpandPrime(_count));

        private void Resize(int newSize)
        {
            Debug.Assert(_entries.Length > 0, "_entries should be non-empty");
            Debug.Assert(newSize >= _entries.Length);

            var count = _count;

            // Rather than creating a copy of _entries, instead reuse as much of it's data as possible.
            var entries = CreateNewSegmentedArrayReusingOldSegments(_entries, newSize);

            // Assign member variables after both arrays allocated to guard against corruption from OOM if second fails
            _buckets = new SegmentedArray<int>(newSize);
            _fastModMultiplier = HashHelpers.GetFastModMultiplier((uint)newSize);
            for (var i = 0; i < count; i++)
            {
                if (entries[i]._next >= -1)
                {
                    ref var bucket = ref GetBucket(entries[i]._hashCode);
                    entries[i]._next = bucket - 1; // Value in _buckets is 1-based
                    bucket = i + 1;
                }
            }

            _entries = entries;
        }

        private static SegmentedArray<Entry> CreateNewSegmentedArrayReusingOldSegments(SegmentedArray<Entry> oldArray, int newSize)
        {
            var segments = SegmentedCollectionsMarshal.AsSegments(oldArray);

            var oldSegmentCount = segments.Length;
            var newSegmentCount = (newSize + SegmentedArrayHelper.GetSegmentSize<Entry>() - 1) >> SegmentedArrayHelper.GetSegmentShift<Entry>();

            // Grow the array of segments, if necessary
            Array.Resize(ref segments, newSegmentCount);

            // Resize all segments to full segment size from the last old segment to the next to last
            // new segment.
            for (var i = oldSegmentCount - 1; i < newSegmentCount - 1; i++)
                Array.Resize(ref segments[i], SegmentedArrayHelper.GetSegmentSize<Entry>());

            // Resize the last segment
            var lastSegmentSize = newSize - ((newSegmentCount - 1) << SegmentedArrayHelper.GetSegmentShift<Entry>());
            Array.Resize(ref segments[newSegmentCount - 1], lastSegmentSize);

            return SegmentedCollectionsMarshal.AsSegmentedArray(newSize, segments);
        }

        public bool Remove(TKey key)
        {
            // The overload Remove(TKey key, out TValue value) is a copy of this method with one additional
            // statement to copy the value for entry being removed into the output parameter.
            // Code has been intentionally duplicated for performance reasons.

            if (key == null)
            {
                ThrowHelper.ThrowArgumentNullException(ExceptionArgument.key);
            }

            if (_buckets.Length > 0)
            {
                Debug.Assert(_entries.Length > 0, "entries should be non-empty");
                uint collisionCount = 0;

                var comparer = _comparer;
                Debug.Assert((SupportsComparerDevirtualization && typeof(TKey).IsValueType) || comparer is not null);
                var hashCode = (uint)(SupportsComparerDevirtualization && typeof(TKey).IsValueType && comparer == null ? key.GetHashCode() : comparer!.GetHashCode(key));

                ref var bucket = ref GetBucket(hashCode);
                var entries = _entries;
                var last = -1;
                var i = bucket - 1; // Value in buckets is 1-based
                while (i >= 0)
                {
                    ref var entry = ref entries[i];

                    if (entry._hashCode == hashCode &&
                        (SupportsComparerDevirtualization && typeof(TKey).IsValueType && comparer == null ? EqualityComparer<TKey>.Default.Equals(entry._key, key) : comparer!.Equals(entry._key, key)))
                    {
                        if (last < 0)
                        {
                            bucket = entry._next + 1; // Value in buckets is 1-based
                        }
                        else
                        {
                            entries[last]._next = entry._next;
                        }

                        Debug.Assert((StartOfFreeList - _freeList) < 0, "shouldn't underflow because max hashtable length is MaxPrimeArrayLength = 0x7FEFFFFD(2146435069) _freelist underflow threshold 2147483646");
                        entry._next = StartOfFreeList - _freeList;

#if NET
                        if (RuntimeHelpers.IsReferenceOrContainsReferences<TKey>())
#endif
                        {
                            entry._key = default!;
                        }

#if NET
                        if (RuntimeHelpers.IsReferenceOrContainsReferences<TValue>())
#endif
                        {
                            entry._value = default!;
                        }

                        _freeList = i;
                        _freeCount++;
                        return true;
                    }

                    last = i;
                    i = entry._next;

                    collisionCount++;
                    if (collisionCount > (uint)entries.Length)
                    {
                        // The chain of entries forms a loop; which means a concurrent update has happened.
                        // Break out of the loop and throw, rather than looping forever.
                        ThrowHelper.ThrowInvalidOperationException_ConcurrentOperationsNotSupported();
                    }
                }
            }
            return false;
        }

        public bool Remove(TKey key, [MaybeNullWhen(false)] out TValue value)
        {
            // This overload is a copy of the overload Remove(TKey key) with one additional
            // statement to copy the value for entry being removed into the output parameter.
            // Code has been intentionally duplicated for performance reasons.

            if (key == null)
            {
                ThrowHelper.ThrowArgumentNullException(ExceptionArgument.key);
            }

            if (_buckets.Length > 0)
            {
                Debug.Assert(_entries.Length > 0, "entries should be non-empty");
                uint collisionCount = 0;

                var comparer = _comparer;
                Debug.Assert((SupportsComparerDevirtualization && typeof(TKey).IsValueType) || comparer is not null);
                var hashCode = (uint)(SupportsComparerDevirtualization && typeof(TKey).IsValueType && comparer == null ? key.GetHashCode() : comparer!.GetHashCode(key));

                ref var bucket = ref GetBucket(hashCode);
                var entries = _entries;
                var last = -1;
                var i = bucket - 1; // Value in buckets is 1-based
                while (i >= 0)
                {
                    ref var entry = ref entries[i];

                    if (entry._hashCode == hashCode &&
                        (SupportsComparerDevirtualization && typeof(TKey).IsValueType && comparer == null ? EqualityComparer<TKey>.Default.Equals(entry._key, key) : comparer!.Equals(entry._key, key)))
                    {
                        if (last < 0)
                        {
                            bucket = entry._next + 1; // Value in buckets is 1-based
                        }
                        else
                        {
                            entries[last]._next = entry._next;
                        }

                        value = entry._value;

                        Debug.Assert((StartOfFreeList - _freeList) < 0, "shouldn't underflow because max hashtable length is MaxPrimeArrayLength = 0x7FEFFFFD(2146435069) _freelist underflow threshold 2147483646");
                        entry._next = StartOfFreeList - _freeList;

#if NET
                        if (RuntimeHelpers.IsReferenceOrContainsReferences<TKey>())
#endif
                        {
                            entry._key = default!;
                        }

#if NET
                        if (RuntimeHelpers.IsReferenceOrContainsReferences<TValue>())
#endif
                        {
                            entry._value = default!;
                        }

                        _freeList = i;
                        _freeCount++;
                        return true;
                    }

                    last = i;
                    i = entry._next;

                    collisionCount++;
                    if (collisionCount > (uint)entries.Length)
                    {
                        // The chain of entries forms a loop; which means a concurrent update has happened.
                        // Break out of the loop and throw, rather than looping forever.
                        ThrowHelper.ThrowInvalidOperationException_ConcurrentOperationsNotSupported();
                    }
                }
            }

            value = default;
            return false;
        }

#pragma warning disable CS8767 // Nullability of reference types in type of parameter doesn't match implicitly implemented member (possibly because of nullability attributes).
        public bool TryGetValue(TKey key, [MaybeNullWhen(false)] out TValue value)
#pragma warning restore CS8767 // Nullability of reference types in type of parameter doesn't match implicitly implemented member (possibly because of nullability attributes).
        {
            ref var valRef = ref FindValue(key);
            if (!RoslynUnsafe.IsNullRef(ref valRef))
            {
                value = valRef;
                return true;
            }

            value = default;
            return false;
        }

        public bool TryAdd(TKey key, TValue value)
            => TryInsert(key, value, InsertionBehavior.None);

        bool ICollection<KeyValuePair<TKey, TValue>>.IsReadOnly => false;

        void ICollection<KeyValuePair<TKey, TValue>>.CopyTo(KeyValuePair<TKey, TValue>[] array, int index)
            => CopyTo(array, index);

        void ICollection.CopyTo(Array array, int index)
        {
            if (array == null)
            {
                ThrowHelper.ThrowArgumentNullException(ExceptionArgument.array);
            }

            if (array.Rank != 1)
            {
                ThrowHelper.ThrowArgumentException(ExceptionResource.Arg_RankMultiDimNotSupported);
            }

            if (array.GetLowerBound(0) != 0)
            {
                ThrowHelper.ThrowArgumentException(ExceptionResource.Arg_NonZeroLowerBound);
            }

            if ((uint)index > (uint)array.Length)
            {
                ThrowHelper.ThrowIndexArgumentOutOfRange_NeedNonNegNumException();
            }

            if (array.Length - index < Count)
            {
                ThrowHelper.ThrowArgumentException(ExceptionResource.Arg_ArrayPlusOffTooSmall);
            }

            if (array is KeyValuePair<TKey, TValue>[] pairs)
            {
                CopyTo(pairs, index);
            }
            else if (array is DictionaryEntry[] dictEntryArray)
            {
                var entries = _entries;
                for (var i = 0; i < _count; i++)
                {
                    if (entries[i]._next >= -1)
                    {
                        dictEntryArray[index++] = new DictionaryEntry(entries[i]._key, entries[i]._value);
                    }
                }
            }
            else
            {
                var objects = array as object[];
                if (objects == null)
                {
                    ThrowHelper.ThrowArgumentException_Argument_IncompatibleArrayType();
                }

                try
                {
                    var count = _count;
                    var entries = _entries;
                    for (var i = 0; i < count; i++)
                    {
                        if (entries[i]._next >= -1)
                        {
                            objects[index++] = new KeyValuePair<TKey, TValue>(entries[i]._key, entries[i]._value);
                        }
                    }
                }
                catch (ArrayTypeMismatchException)
                {
                    ThrowHelper.ThrowArgumentException_Argument_IncompatibleArrayType();
                }
            }
        }

        IEnumerator IEnumerable.GetEnumerator()
            => ((IEnumerable<KeyValuePair<TKey, TValue>>)this).GetEnumerator();

        /// <summary>
        /// Ensures that the dictionary can hold up to 'capacity' entries without any further expansion of its backing storage
        /// </summary>
        public int EnsureCapacity(int capacity)
        {
            if (capacity < 0)
            {
                ThrowHelper.ThrowArgumentOutOfRangeException(ExceptionArgument.capacity);
            }

            var currentCapacity = _entries.Length;
            if (currentCapacity >= capacity)
            {
                return currentCapacity;
            }

            _version++;

            if (_buckets.Length == 0)
            {
                return Initialize(capacity);
            }

            var newSize = HashHelpers.GetPrime(capacity);
            Resize(newSize);
            return newSize;
        }

        /// <summary>
        /// Sets the capacity of this dictionary to what it would be if it had been originally initialized with all its entries
        /// </summary>
        /// <remarks>
        /// This method can be used to minimize the memory overhead
        /// once it is known that no new elements will be added.
        ///
        /// To allocate minimum size storage array, execute the following statements:
        ///
        /// dictionary.Clear();
        /// dictionary.TrimExcess();
        /// </remarks>
        public void TrimExcess()
            => TrimExcess(Count);

        /// <summary>
        /// Sets the capacity of this dictionary to hold up 'capacity' entries without any further expansion of its backing storage
        /// </summary>
        /// <remarks>
        /// This method can be used to minimize the memory overhead
        /// once it is known that no new elements will be added.
        /// </remarks>
        public void TrimExcess(int capacity)
        {
            if (capacity < Count)
            {
                ThrowHelper.ThrowArgumentOutOfRangeException(ExceptionArgument.capacity);
            }

            var newSize = HashHelpers.GetPrime(capacity);
            var oldEntries = _entries;
            var currentCapacity = oldEntries.Length;
            if (newSize >= currentCapacity)
            {
                return;
            }

            var oldCount = _count;
            _version++;
            Initialize(newSize);

            CopyEntries(oldEntries, oldCount);
        }

        private void CopyEntries(SegmentedArray<Entry> entries, int count)
        {
            var newEntries = _entries;
            var newCount = 0;
            for (var i = 0; i < count; i++)
            {
                var hashCode = entries[i]._hashCode;
                if (entries[i]._next >= -1)
                {
                    ref var entry = ref newEntries[newCount];
                    entry = entries[i];
                    ref var bucket = ref GetBucket(hashCode);
                    entry._next = bucket - 1; // Value in _buckets is 1-based
                    bucket = newCount + 1;
                    newCount++;
                }
            }

            _count = newCount;
            _freeCount = 0;
        }

        bool ICollection.IsSynchronized => false;

        object ICollection.SyncRoot => this;

        bool IDictionary.IsFixedSize => false;

        bool IDictionary.IsReadOnly => false;

        ICollection IDictionary.Keys => Keys;

        ICollection IDictionary.Values => Values;

        object? IDictionary.this[object key]
        {
            get
            {
                if (IsCompatibleKey(key))
                {
                    ref var value = ref FindValue((TKey)key);
                    if (!RoslynUnsafe.IsNullRef(ref value))
                    {
                        return value;
                    }
                }

                return null;
            }
            set
            {
                if (key == null)
                {
                    ThrowHelper.ThrowArgumentNullException(ExceptionArgument.key);
                }
                ThrowHelper.IfNullAndNullsAreIllegalThenThrow<TValue>(value, ExceptionArgument.value);

                try
                {
                    var tempKey = (TKey)key;
                    try
                    {
                        this[tempKey] = (TValue)value!;
                    }
                    catch (InvalidCastException)
                    {
                        ThrowHelper.ThrowWrongValueTypeArgumentException(value, typeof(TValue));
                    }
                }
                catch (InvalidCastException)
                {
                    ThrowHelper.ThrowWrongKeyTypeArgumentException(key, typeof(TKey));
                }
            }
        }

        private static bool IsCompatibleKey(object key)
        {
            if (key == null)
            {
                ThrowHelper.ThrowArgumentNullException(ExceptionArgument.key);
            }
            return key is TKey;
        }

        void IDictionary.Add(object key, object? value)
        {
            if (key == null)
            {
                ThrowHelper.ThrowArgumentNullException(ExceptionArgument.key);
            }
            ThrowHelper.IfNullAndNullsAreIllegalThenThrow<TValue>(value, ExceptionArgument.value);

            try
            {
                var tempKey = (TKey)key;

                try
                {
                    Add(tempKey, (TValue)value!);
                }
                catch (InvalidCastException)
                {
                    ThrowHelper.ThrowWrongValueTypeArgumentException(value, typeof(TValue));
                }
            }
            catch (InvalidCastException)
            {
                ThrowHelper.ThrowWrongKeyTypeArgumentException(key, typeof(TKey));
            }
        }

        bool IDictionary.Contains(object key)
        {
            if (IsCompatibleKey(key))
            {
                return ContainsKey((TKey)key);
            }

            return false;
        }

        IDictionaryEnumerator IDictionary.GetEnumerator()
            => new Enumerator(this, Enumerator.DictEntry);

        void IDictionary.Remove(object key)
        {
            if (IsCompatibleKey(key))
            {
                Remove((TKey)key);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private ref int GetBucket(uint hashCode)
        {
            var buckets = _buckets;
            return ref buckets[(int)HashHelpers.FastMod(hashCode, (uint)buckets.Length, _fastModMultiplier)];
        }

        private struct Entry
        {
            public uint _hashCode;
            /// <summary>
            /// 0-based index of next entry in chain: -1 means end of chain
            /// also encodes whether this entry _itself_ is part of the free list by changing sign and subtracting 3,
            /// so -2 means end of free list, -3 means index 0 but on free list, -4 means index 1 but on free list, etc.
            /// </summary>
            public int _next;
            public TKey _key;     // Key of entry
            public TValue _value; // Value of entry
        }

        public struct Enumerator : IEnumerator<KeyValuePair<TKey, TValue>>, IDictionaryEnumerator
        {
            private readonly SegmentedDictionary<TKey, TValue> _dictionary;
            private readonly int _version;
            private int _index;
            private KeyValuePair<TKey, TValue> _current;
            private readonly int _getEnumeratorRetType;  // What should Enumerator.Current return?

            internal const int DictEntry = 1;
            internal const int KeyValuePair = 2;

            internal Enumerator(SegmentedDictionary<TKey, TValue> dictionary, int getEnumeratorRetType)
            {
                _dictionary = dictionary;
                _version = dictionary._version;
                _index = 0;
                _getEnumeratorRetType = getEnumeratorRetType;
                _current = default;
            }

            public bool MoveNext()
            {
                if (_version != _dictionary._version)
                {
                    ThrowHelper.ThrowInvalidOperationException_InvalidOperation_EnumFailedVersion();
                }

                // Use unsigned comparison since we set index to dictionary.count+1 when the enumeration ends.
                // dictionary.count+1 could be negative if dictionary.count is int.MaxValue
                while ((uint)_index < (uint)_dictionary._count)
                {
                    ref var entry = ref _dictionary._entries[_index++];

                    if (entry._next >= -1)
                    {
                        _current = new KeyValuePair<TKey, TValue>(entry._key, entry._value);
                        return true;
                    }
                }

                _index = _dictionary._count + 1;
                _current = default;
                return false;
            }

            public readonly KeyValuePair<TKey, TValue> Current => _current;

            public readonly void Dispose()
            {
            }

            readonly object? IEnumerator.Current
            {
                get
                {
                    if (_index == 0 || (_index == _dictionary._count + 1))
                    {
                        ThrowHelper.ThrowInvalidOperationException_InvalidOperation_EnumOpCantHappen();
                    }

                    if (_getEnumeratorRetType == DictEntry)
                    {
                        return new DictionaryEntry(_current.Key, _current.Value);
                    }

                    return new KeyValuePair<TKey, TValue>(_current.Key, _current.Value);
                }
            }

            void IEnumerator.Reset()
            {
                if (_version != _dictionary._version)
                {
                    ThrowHelper.ThrowInvalidOperationException_InvalidOperation_EnumFailedVersion();
                }

                _index = 0;
                _current = default;
            }

            readonly DictionaryEntry IDictionaryEnumerator.Entry
            {
                get
                {
                    if (_index == 0 || (_index == _dictionary._count + 1))
                    {
                        ThrowHelper.ThrowInvalidOperationException_InvalidOperation_EnumOpCantHappen();
                    }

                    return new DictionaryEntry(_current.Key, _current.Value);
                }
            }

            readonly object IDictionaryEnumerator.Key
            {
                get
                {
                    if (_index == 0 || (_index == _dictionary._count + 1))
                    {
                        ThrowHelper.ThrowInvalidOperationException_InvalidOperation_EnumOpCantHappen();
                    }

                    return _current.Key;
                }
            }

            readonly object? IDictionaryEnumerator.Value
            {
                get
                {
                    if (_index == 0 || (_index == _dictionary._count + 1))
                    {
                        ThrowHelper.ThrowInvalidOperationException_InvalidOperation_EnumOpCantHappen();
                    }

                    return _current.Value;
                }
            }
        }

        [DebuggerTypeProxy(typeof(DictionaryKeyCollectionDebugView<,>))]
        [DebuggerDisplay("Count = {Count}")]
        public sealed class KeyCollection : ICollection<TKey>, ICollection, IReadOnlyCollection<TKey>
        {
            private static IEnumerator<TKey>? s_emptyEnumerator;

            private readonly SegmentedDictionary<TKey, TValue> _dictionary;

            public KeyCollection(SegmentedDictionary<TKey, TValue> dictionary)
            {
                if (dictionary == null)
                {
                    ThrowHelper.ThrowArgumentNullException(ExceptionArgument.dictionary);
                }

                _dictionary = dictionary;
            }

            public Enumerator GetEnumerator()
                => new Enumerator(_dictionary);

            public void CopyTo(TKey[] array, int index)
            {
                if (array == null)
                {
                    ThrowHelper.ThrowArgumentNullException(ExceptionArgument.array);
                }

                if (index < 0 || index > array.Length)
                {
                    ThrowHelper.ThrowIndexArgumentOutOfRange_NeedNonNegNumException();
                }

                if (array.Length - index < _dictionary.Count)
                {
                    ThrowHelper.ThrowArgumentException(ExceptionResource.Arg_ArrayPlusOffTooSmall);
                }

                var count = _dictionary._count;
                var entries = _dictionary._entries;
                for (var i = 0; i < count; i++)
                {
                    if (entries[i]._next >= -1)
                        array[index++] = entries[i]._key;
                }
            }

            public int Count => _dictionary.Count;

            bool ICollection<TKey>.IsReadOnly => true;

            void ICollection<TKey>.Add(TKey item)
                => ThrowHelper.ThrowNotSupportedException(ExceptionResource.NotSupported_KeyCollectionSet);

            void ICollection<TKey>.Clear()
                => ThrowHelper.ThrowNotSupportedException(ExceptionResource.NotSupported_KeyCollectionSet);

            public bool Contains(TKey item)
                => _dictionary.ContainsKey(item);

            bool ICollection<TKey>.Remove(TKey item)
            {
                ThrowHelper.ThrowNotSupportedException(ExceptionResource.NotSupported_KeyCollectionSet);
                return false;
            }

            IEnumerator<TKey> IEnumerable<TKey>.GetEnumerator() =>
                Count == 0 ? GetEmptyEnumerator() :
                GetEnumerator();

            private static IEnumerator<TKey> GetEmptyEnumerator()
            {
                return LazyInitializer.EnsureInitialized(ref s_emptyEnumerator, static () => new Enumerator(new SegmentedDictionary<TKey, TValue>()))!;
            }

            IEnumerator IEnumerable.GetEnumerator()
                => ((IEnumerable<TKey>)this).GetEnumerator();

            void ICollection.CopyTo(Array array, int index)
            {
                if (array == null)
                {
                    ThrowHelper.ThrowArgumentNullException(ExceptionArgument.array);
                }

                if (array.Rank != 1)
                {
                    ThrowHelper.ThrowArgumentException(ExceptionResource.Arg_RankMultiDimNotSupported);
                }

                if (array.GetLowerBound(0) != 0)
                {
                    ThrowHelper.ThrowArgumentException(ExceptionResource.Arg_NonZeroLowerBound);
                }

                if ((uint)index > (uint)array.Length)
                {
                    ThrowHelper.ThrowIndexArgumentOutOfRange_NeedNonNegNumException();
                }

                if (array.Length - index < _dictionary.Count)
                {
                    ThrowHelper.ThrowArgumentException(ExceptionResource.Arg_ArrayPlusOffTooSmall);
                }

                if (array is TKey[] keys)
                {
                    CopyTo(keys, index);
                }
                else
                {
                    var objects = array as object[];
                    if (objects == null)
                    {
                        ThrowHelper.ThrowArgumentException_Argument_IncompatibleArrayType();
                    }

                    var count = _dictionary._count;
                    var entries = _dictionary._entries;
                    try
                    {
                        for (var i = 0; i < count; i++)
                        {
                            if (entries[i]._next >= -1)
                                objects[index++] = entries[i]._key;
                        }
                    }
                    catch (ArrayTypeMismatchException)
                    {
                        ThrowHelper.ThrowArgumentException_Argument_IncompatibleArrayType();
                    }
                }
            }

            bool ICollection.IsSynchronized => false;

            object ICollection.SyncRoot => ((ICollection)_dictionary).SyncRoot;

            public struct Enumerator : IEnumerator<TKey>, IEnumerator
            {
                private readonly SegmentedDictionary<TKey, TValue> _dictionary;
                private int _index;
                private readonly int _version;
                private TKey? _currentKey;

                internal Enumerator(SegmentedDictionary<TKey, TValue> dictionary)
                {
                    _dictionary = dictionary;
                    _version = dictionary._version;
                    _index = 0;
                    _currentKey = default;
                }

                public readonly void Dispose()
                {
                }

                public bool MoveNext()
                {
                    if (_version != _dictionary._version)
                    {
                        ThrowHelper.ThrowInvalidOperationException_InvalidOperation_EnumFailedVersion();
                    }

                    while ((uint)_index < (uint)_dictionary._count)
                    {
                        ref var entry = ref _dictionary._entries[_index++];

                        if (entry._next >= -1)
                        {
                            _currentKey = entry._key;
                            return true;
                        }
                    }

                    _index = _dictionary._count + 1;
                    _currentKey = default;
                    return false;
                }

                public readonly TKey Current => _currentKey!;

                readonly object? IEnumerator.Current
                {
                    get
                    {
                        if (_index == 0 || (_index == _dictionary._count + 1))
                        {
                            ThrowHelper.ThrowInvalidOperationException_InvalidOperation_EnumOpCantHappen();
                        }

                        return _currentKey;
                    }
                }

                void IEnumerator.Reset()
                {
                    if (_version != _dictionary._version)
                    {
                        ThrowHelper.ThrowInvalidOperationException_InvalidOperation_EnumFailedVersion();
                    }

                    _index = 0;
                    _currentKey = default;
                }
            }
        }

        [DebuggerTypeProxy(typeof(DictionaryValueCollectionDebugView<,>))]
        [DebuggerDisplay("Count = {Count}")]
        public sealed class ValueCollection : ICollection<TValue>, ICollection, IReadOnlyCollection<TValue>
        {
            private static IEnumerator<TValue>? s_emptyEnumerator;

            private readonly SegmentedDictionary<TKey, TValue> _dictionary;

            public ValueCollection(SegmentedDictionary<TKey, TValue> dictionary)
            {
                if (dictionary == null)
                {
                    ThrowHelper.ThrowArgumentNullException(ExceptionArgument.dictionary);
                }

                _dictionary = dictionary;
            }

            public Enumerator GetEnumerator()
                => new Enumerator(_dictionary);

            public void CopyTo(TValue[] array, int index)
            {
                if (array == null)
                {
                    ThrowHelper.ThrowArgumentNullException(ExceptionArgument.array);
                }

                if ((uint)index > array.Length)
                {
                    ThrowHelper.ThrowIndexArgumentOutOfRange_NeedNonNegNumException();
                }

                if (array.Length - index < _dictionary.Count)
                {
                    ThrowHelper.ThrowArgumentException(ExceptionResource.Arg_ArrayPlusOffTooSmall);
                }

                var count = _dictionary._count;
                var entries = _dictionary._entries;
                for (var i = 0; i < count; i++)
                {
                    if (entries[i]._next >= -1)
                        array[index++] = entries[i]._value;
                }
            }

            public int Count => _dictionary.Count;

            bool ICollection<TValue>.IsReadOnly => true;

            void ICollection<TValue>.Add(TValue item)
                => ThrowHelper.ThrowNotSupportedException(ExceptionResource.NotSupported_ValueCollectionSet);

            bool ICollection<TValue>.Remove(TValue item)
            {
                ThrowHelper.ThrowNotSupportedException(ExceptionResource.NotSupported_ValueCollectionSet);
                return false;
            }

            void ICollection<TValue>.Clear()
                => ThrowHelper.ThrowNotSupportedException(ExceptionResource.NotSupported_ValueCollectionSet);

            bool ICollection<TValue>.Contains(TValue item)
                => _dictionary.ContainsValue(item);

            IEnumerator<TValue> IEnumerable<TValue>.GetEnumerator() =>
                Count == 0 ? GetEmptyEnumerator() :
                GetEnumerator();

            private static IEnumerator<TValue> GetEmptyEnumerator()
            {
                return LazyInitializer.EnsureInitialized(ref s_emptyEnumerator, static () => new Enumerator(new SegmentedDictionary<TKey, TValue>()))!;
            }

            IEnumerator IEnumerable.GetEnumerator()
                => ((IEnumerable<TValue>)this).GetEnumerator();

            void ICollection.CopyTo(Array array, int index)
            {
                if (array == null)
                {
                    ThrowHelper.ThrowArgumentNullException(ExceptionArgument.array);
                }

                if (array.Rank != 1)
                {
                    ThrowHelper.ThrowArgumentException(ExceptionResource.Arg_RankMultiDimNotSupported);
                }

                if (array.GetLowerBound(0) != 0)
                {
                    ThrowHelper.ThrowArgumentException(ExceptionResource.Arg_NonZeroLowerBound);
                }

                if ((uint)index > (uint)array.Length)
                {
                    ThrowHelper.ThrowIndexArgumentOutOfRange_NeedNonNegNumException();
                }

                if (array.Length - index < _dictionary.Count)
                {
                    ThrowHelper.ThrowArgumentException(ExceptionResource.Arg_ArrayPlusOffTooSmall);
                }

                if (array is TValue[] values)
                {
                    CopyTo(values, index);
                }
                else
                {
                    var objects = array as object[];
                    if (objects == null)
                    {
                        ThrowHelper.ThrowArgumentException_Argument_IncompatibleArrayType();
                    }

                    var count = _dictionary._count;
                    var entries = _dictionary._entries;
                    try
                    {
                        for (var i = 0; i < count; i++)
                        {
                            if (entries[i]._next >= -1)
                                objects[index++] = entries[i]._value!;
                        }
                    }
                    catch (ArrayTypeMismatchException)
                    {
                        ThrowHelper.ThrowArgumentException_Argument_IncompatibleArrayType();
                    }
                }
            }

            bool ICollection.IsSynchronized => false;

            object ICollection.SyncRoot => ((ICollection)_dictionary).SyncRoot;

            public struct Enumerator : IEnumerator<TValue>, IEnumerator
            {
                private readonly SegmentedDictionary<TKey, TValue> _dictionary;
                private int _index;
                private readonly int _version;
                private TValue? _currentValue;

                internal Enumerator(SegmentedDictionary<TKey, TValue> dictionary)
                {
                    _dictionary = dictionary;
                    _version = dictionary._version;
                    _index = 0;
                    _currentValue = default;
                }

                public readonly void Dispose()
                {
                }

                public bool MoveNext()
                {
                    if (_version != _dictionary._version)
                    {
                        ThrowHelper.ThrowInvalidOperationException_InvalidOperation_EnumFailedVersion();
                    }

                    while ((uint)_index < (uint)_dictionary._count)
                    {
                        ref var entry = ref _dictionary._entries[_index++];

                        if (entry._next >= -1)
                        {
                            _currentValue = entry._value;
                            return true;
                        }
                    }
                    _index = _dictionary._count + 1;
                    _currentValue = default;
                    return false;
                }

                public readonly TValue Current => _currentValue!;

                readonly object? IEnumerator.Current
                {
                    get
                    {
                        if (_index == 0 || (_index == _dictionary._count + 1))
                        {
                            ThrowHelper.ThrowInvalidOperationException_InvalidOperation_EnumOpCantHappen();
                        }

                        return _currentValue;
                    }
                }

                void IEnumerator.Reset()
                {
                    if (_version != _dictionary._version)
                    {
                        ThrowHelper.ThrowInvalidOperationException_InvalidOperation_EnumFailedVersion();
                    }

                    _index = 0;
                    _currentValue = default;
                }
            }
        }
    }
}
