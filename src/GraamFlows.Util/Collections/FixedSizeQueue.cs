namespace GraamFlows.Util.Collections;

public class FixedSizedQueue<T> : Queue<T>
{
    public FixedSizedQueue(int size)
    {
        Size = size;
    }

    public int Size { get; }

    public new void Enqueue(T obj)
    {
        base.Enqueue(obj);
        while (Count > Size) Dequeue();
    }
}