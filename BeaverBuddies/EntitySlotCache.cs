using System;

namespace BeaverBuddies
{
    // Remembers, for each position in a list of entities, which entity was there and what a lookup
    // returned for it, so a lookup whose answer never changes for an entity is done once and not on
    // every tick.
    //
    // A position is only trusted while the very same entity object is still at it (a reference
    // comparison). When the list changes, an added or removed entity shifts everything after it, so
    // those positions simply miss and are looked up again; there is nothing to invalidate. "No
    // result" (null) is remembered like any other result.
    public sealed class EntitySlotCache<TEntity, TValue> where TEntity : class
    {
        private TEntity[] _entities = new TEntity[0];
        private TValue[] _values = new TValue[0];
        // One past the highest position written, so Trim only visits positions that were used.
        private int _used;

        public bool TryGet(int index, TEntity entity, out TValue value)
        {
            if (entity != null && (uint)index < (uint)_entities.Length && ReferenceEquals(_entities[index], entity))
            {
                value = _values[index];
                return true;
            }
            value = default;
            return false;
        }

        public void Set(int index, TEntity entity, TValue value)
        {
            if (index < 0) throw new ArgumentOutOfRangeException(nameof(index));
            if (index >= _entities.Length)
            {
                int size = Math.Max(Math.Max(index + 1, _entities.Length * 2), 16);
                Array.Resize(ref _entities, size);
                Array.Resize(ref _values, size);
            }
            _entities[index] = entity;
            _values[index] = value;
            if (index >= _used) _used = index + 1;
        }

        // Forgets the positions at or beyond count, for when the list got shorter, so entities that
        // were removed are not kept alive by this cache.
        public void Trim(int count)
        {
            if (count < 0) count = 0;
            for (int i = count; i < _used; i++)
            {
                _entities[i] = null;
                _values[i] = default;
            }
            if (count < _used) _used = count;
        }
    }
}
