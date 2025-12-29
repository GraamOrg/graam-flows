using System.Collections;
using System.Diagnostics;

namespace GraamFlows.Objects.DataObjects;

public class Cashflows : IEnumerable<IAssetCashflow>

{
    private IAssetCashflow[] _cashflows;

    public Cashflows(int capacity)
    {
        _cashflows = new IAssetCashflow[capacity];
        Count = 0;
    }

    public int Count { get; private set; }
    public int Capacity => _cashflows.Length;

    public IAssetCashflow this[int index] => ElementAt(index);

    public IEnumerator<IAssetCashflow> GetEnumerator()
    {
        return new CashflowEnumerator(this);
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }

    public void Add(IAssetCashflow cashflow)
    {
        if (Count == _cashflows.Length)
            Resize(2 * _cashflows.Length);

        var index = Count++;
        IAssetCashflow flow;
        if ((flow = _cashflows[index]) == null)
            _cashflows[index] = flow = new Cashflow();
        flow.Assign(cashflow);
    }

    public IAssetCashflow Add()
    {
        if (Count == _cashflows.Length)
            Resize(2 * _cashflows.Length);
        return ElementAt(Count++);
    }

    public void Resize(int size)
    {
        Array.Resize(ref _cashflows, size);
        if (Count > size)
            Count = size;
    }

    public IAssetCashflow ElementAt(int index)
    {
        Debug.Assert(index < _cashflows.Length);
        IAssetCashflow flow;
        if ((flow = _cashflows[index]) == null)
            _cashflows[index] = flow = new Cashflow();
        return flow;
    }

    public void Clear()
    {
        for (var i = 0; i != Count; ++i) _cashflows[i] = null;
        Count = 0;
    }

    public void Aggregate(Cashflows cashflows)
    {
        var num = Math.Min(Count, cashflows.Count);
        for (var i = 0; i < num; i++)
            ElementAt(i).Aggregate(cashflows.ElementAt(i));

        if (Count < cashflows.Count)
        {
            Resize(cashflows.Count);
            for (var i = Count; i < cashflows.Count; i++)
                Add(cashflows.ElementAt(i));
        }
    }

    private class CashflowEnumerator : IEnumerator<IAssetCashflow>
    {
        private readonly Cashflows _cashflows;
        private int _index;

        internal CashflowEnumerator(Cashflows cashflows)
        {
            _index = -1;
            _cashflows = cashflows;
        }

        public bool MoveNext()
        {
            _index++;
            return _index < _cashflows.Count;
        }

        public void Reset()
        {
            _index = -1;
        }

        object IEnumerator.Current => Current;

        public IAssetCashflow Current
        {
            get
            {
                try
                {
                    return _cashflows[_index];
                }
                catch (IndexOutOfRangeException)
                {
                    throw new InvalidOperationException();
                }
            }
        }

        public void Dispose()
        {
        }
    }
}