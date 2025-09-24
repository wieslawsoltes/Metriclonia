using System;
using System.Buffers;
using System.Collections.Generic;

namespace Metriclonia.Monitor.Infrastructure;

internal static class TagFormatter
{
    private const int InitialCharBuffer = 256;

    public static string BuildSignature(IReadOnlyDictionary<string, string?>? tags)
    {
        if (tags is null || tags.Count == 0)
        {
            return string.Empty;
        }

        var buffer = ArrayPool<TagProjection>.Shared.Rent(tags.Count);
        try
        {
            var span = buffer.AsSpan(0, tags.Count);
            var index = 0;
            foreach (var kvp in tags)
            {
                span[index++] = new TagProjection(kvp.Key, kvp.Value);
            }

            return BuildFromSortedSpan(span);
        }
        finally
        {
            ArrayPool<TagProjection>.Shared.Return(buffer, clearArray: true);
        }
    }

    public static string BuildSignature(IEnumerable<KeyValuePair<string, string?>> tags)
    {
        if (tags is IReadOnlyDictionary<string, string?> dictionary)
        {
            return BuildSignature(dictionary);
        }

        if (tags is ICollection<KeyValuePair<string, string?>> collection)
        {
            if (collection.Count == 0)
            {
                return string.Empty;
            }

            var buffer = ArrayPool<TagProjection>.Shared.Rent(collection.Count);
            try
            {
                var span = buffer.AsSpan(0, collection.Count);
                var index = 0;
                foreach (var kvp in collection)
                {
                    span[index++] = new TagProjection(kvp.Key, kvp.Value);
                }

                return BuildFromSortedSpan(span);
            }
            finally
            {
                ArrayPool<TagProjection>.Shared.Return(buffer, clearArray: true);
            }
        }

        var list = new List<KeyValuePair<string, string?>>(tags);
        if (list.Count == 0)
        {
            return string.Empty;
        }

        var rented = ArrayPool<TagProjection>.Shared.Rent(list.Count);
        try
        {
            var span = rented.AsSpan(0, list.Count);
            for (var i = 0; i < list.Count; i++)
            {
                var kvp = list[i];
                span[i] = new TagProjection(kvp.Key, kvp.Value);
            }

            return BuildFromSortedSpan(span);
        }
        finally
        {
            ArrayPool<TagProjection>.Shared.Return(rented, clearArray: true);
        }
    }

    private static string BuildFromSortedSpan(Span<TagProjection> slice)
    {
        slice.Sort(TagProjectionComparer.Instance);

        Span<char> initial = stackalloc char[InitialCharBuffer];
        var builder = new PooledStringBuilder(initial);
        try
        {
            for (var i = 0; i < slice.Length; i++)
            {
                if (i > 0)
                {
                    builder.Append(", ");
                }

                ref readonly var tag = ref slice[i];
                builder.Append(tag.Key);
                if (!string.IsNullOrWhiteSpace(tag.Value))
                {
                    builder.Append('=');
                    builder.Append(tag.Value!);
                }
            }

            return builder.ToString();
        }
        finally
        {
            builder.Dispose();
        }
    }

    private readonly record struct TagProjection(string Key, string? Value);

    private sealed class TagProjectionComparer : IComparer<TagProjection>
    {
        public static readonly TagProjectionComparer Instance = new();

        public int Compare(TagProjection x, TagProjection y)
            => string.Compare(x.Key, y.Key, StringComparison.Ordinal);
    }

    private ref struct PooledStringBuilder
    {
        private Span<char> _buffer;
        private char[]? _pooled;
        private int _position;

        public PooledStringBuilder(Span<char> initial)
        {
            _buffer = initial;
            _pooled = null;
            _position = 0;
        }

        public void Append(char value)
        {
            EnsureCapacity(1);
            _buffer[_position++] = value;
        }

        public void Append(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return;
            }

            Append(value.AsSpan());
        }

        public void Append(ReadOnlySpan<char> value)
        {
            EnsureCapacity(value.Length);
            value.CopyTo(_buffer[_position..]);
            _position += value.Length;
        }

        private void EnsureCapacity(int additional)
        {
            if (_position + additional <= _buffer.Length)
            {
                return;
            }

            Grow(Math.Max(_buffer.Length * 2, _position + additional));
        }

        private void Grow(int newSize)
        {
            var pool = ArrayPool<char>.Shared;
            var rented = pool.Rent(newSize);
            var span = rented.AsSpan();
            _buffer[.._position].CopyTo(span);

            if (_pooled is not null)
            {
                pool.Return(_pooled);
            }

            _buffer = span;
            _pooled = rented;
        }

        public override string ToString()
            => _buffer[.._position].ToString();

        public void Dispose()
        {
            if (_pooled is not null)
            {
                ArrayPool<char>.Shared.Return(_pooled);
                _pooled = null;
            }
        }
    }
}
