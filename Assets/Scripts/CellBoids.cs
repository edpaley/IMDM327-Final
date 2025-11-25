using Unity.VisualScripting;
using UnityEngine;
using UnityEngine.Rendering;

public class CellBoids : MonoBehaviour
{
    [SerializeField] int NumBoids = 100;
    [SerializeField][Range(0.01f, .5f)] float BoidRange;
    [SerializeField] ComputeShader CellBoidsCompute;

    private float WorldXY = 1f, WorldXYRange = 0.5f;
    private float CellSize = 1f / 8f;
    private int CellSeats = 8, NumCells = 512;

    private ComputeBuffer Boids;
    private ComputeBuffer BoidCells;
    private ComputeBuffer BoidCellsTemp;
    private ComputeBuffer CellStart;
    private ComputeBuffer CellEnd;

    private ComputeBuffer BucketCounts;
    private ComputeBuffer BucketOffsets;
    private ComputeBuffer BucketCounters;

    private const uint NullCell = 0xFFFFFFFF;

    private int updateKernel, assignCellKernel, clearCellKernel, radixClearKernel, radixHistKernel, radixPrefixKernel, radixScatterKernel, buildRangesKernel;

    private int boidThreads = 256, cellThreads = 16;
    private int boidGroup;

    private string log;
    uint[,] count;

    struct Boid
    {
        public Vector2 pos;
        public Vector2 vel;
    }

    struct BoidCell
    {
        public uint boidIndex;
        public uint cellIndex;
    }

    void Start()
    {
        if (CellBoidsCompute == null)
        {
            Debug.LogWarning("CellBoidsCompute not set");
            return;
        }


        IntializeKernelIndicies();
        InitializeBuffers();
        SetNonUpdatingParams();

        CellBoidsCompute.Dispatch(assignCellKernel, boidGroup, 1, 1);
        SortBoidsByCell();
        //PrintCellCounts();
        Debug.Log(log);
    }

    void Update()
    {
        // CellBoidsCompute.Dispatch(assignCellKernel, boidGroup, 1, 1);
        // SortBoidsByCell();
        // PrintCellCounts();

    }

    private void IntializeKernelIndicies()
    {
        updateKernel = CellBoidsCompute.FindKernel("UpdateBoids");
        assignCellKernel = CellBoidsCompute.FindKernel("AssignCells");
        clearCellKernel = CellBoidsCompute.FindKernel("ClearCells");
        radixClearKernel = CellBoidsCompute.FindKernel("RadixClear");
        radixHistKernel = CellBoidsCompute.FindKernel("RadixHistogram");
        radixPrefixKernel = CellBoidsCompute.FindKernel("RadixPrefixSum");
        radixScatterKernel = CellBoidsCompute.FindKernel("RadixScatter");
        buildRangesKernel = CellBoidsCompute.FindKernel("BuildCellRanges");

    }

    private void InitializeBuffers()
    {
        //BOIDS
        boidGroup = (NumBoids + boidThreads - 1) / boidThreads;

        Boids = new ComputeBuffer(NumBoids, sizeof(float) * 4);

        Boid[] boids = new Boid[NumBoids];
        for (int i = 0; i < NumBoids; i++)
        {
            boids[i].pos = new Vector2(Random.Range(0, WorldXY), Random.Range(0, WorldXY));
            boids[i].vel = Random.insideUnitCircle;
        }

        Boids.SetData(boids);

        count = new uint[8, 8];
        for (int i = 0; i < NumBoids; i++)
        {
            count[(int)(boids[i].pos.x / CellSize), (int)(boids[i].pos.y / CellSize)]++;
        }
        // for (int i = 0; i < 8; i++)
        // {
        //     for (int j = 0; j < 8; j++)
        //     {
        //         Debug.Log($"[{i},{j}]: {count[i, j]}");
        //     }
        // }


        //BOID CELLS
        BoidCells = new ComputeBuffer(NumBoids, sizeof(uint) * 2);
        BoidCellsTemp = new ComputeBuffer(NumBoids, sizeof(uint) * 2);

        BoidCell[] boidCells = new BoidCell[NumBoids];
        for (int i = 0; i < NumBoids; i++)
        {
            boidCells[i].boidIndex = (uint)i;
            boidCells[i].cellIndex = 0u;
        }

        BoidCells.SetData(boidCells);
        BoidCellsTemp.SetData(boidCells);

        //CELL START/END
        CellStart = new ComputeBuffer(NumCells, sizeof(uint));
        CellEnd = new ComputeBuffer(NumCells, sizeof(uint));

        uint[] cellStart = new uint[NumCells];
        uint[] cellEnd = new uint[NumCells];
        for (int i = 0; i < NumCells; i++)
        {
            cellStart[i] = NullCell;
            cellEnd[i] = NullCell;
        }

        CellStart.SetData(cellStart);
        CellEnd.SetData(cellEnd);

        //RADIX
        int radix = 1 << 4;
        BucketCounts = new ComputeBuffer(radix, sizeof(uint));
        BucketOffsets = new ComputeBuffer(radix, sizeof(uint));
        BucketCounters = new ComputeBuffer(radix, sizeof(uint));

        //Buffer bindings

        // AssignCells uses Boids -> writes BoidCells
        CellBoidsCompute.SetBuffer(assignCellKernel, "Boids", Boids);
        CellBoidsCompute.SetBuffer(assignCellKernel, "BoidCells", BoidCells);

        CellBoidsCompute.SetBuffer(clearCellKernel, "CellStart", CellStart);
        CellBoidsCompute.SetBuffer(clearCellKernel, "CellEnd", CellEnd);

        // histogram/scatter kernels use BoidCells / BoidCellsTemp
        CellBoidsCompute.SetBuffer(radixHistKernel, "BoidCells", BoidCells);
        CellBoidsCompute.SetBuffer(radixScatterKernel, "BoidCells", BoidCells);
        CellBoidsCompute.SetBuffer(radixScatterKernel, "BoidCellsTemp", BoidCellsTemp);

        // bind bucket buffers to kernels
        CellBoidsCompute.SetBuffer(radixClearKernel, "BucketCounts", BucketCounts);
        CellBoidsCompute.SetBuffer(radixClearKernel, "BucketCounters", BucketCounters);
        CellBoidsCompute.SetBuffer(radixClearKernel, "BucketOffsets", BucketOffsets);

        CellBoidsCompute.SetBuffer(radixHistKernel, "BucketCounts", BucketCounts);
        CellBoidsCompute.SetBuffer(radixPrefixKernel, "BucketCounts", BucketCounts);
        CellBoidsCompute.SetBuffer(radixPrefixKernel, "BucketOffsets", BucketOffsets);

        CellBoidsCompute.SetBuffer(radixScatterKernel, "BucketOffsets", BucketOffsets);
        CellBoidsCompute.SetBuffer(radixScatterKernel, "BucketCounters", BucketCounters);

        // build ranges uses sorted BoidCells (we will re-bind before dispatch)
        CellBoidsCompute.SetBuffer(buildRangesKernel, "BoidCells", BoidCells);
        CellBoidsCompute.SetBuffer(buildRangesKernel, "CellStart", CellStart);
        CellBoidsCompute.SetBuffer(buildRangesKernel, "CellEnd", CellEnd);


    }

    private void SetNonUpdatingParams()
    {
        CellBoidsCompute.SetInt("_NumBoids", NumBoids);
        CellBoidsCompute.SetFloat("_CellSize", CellSize);
        CellBoidsCompute.SetInt("_CellSeats", CellSeats);
        CellBoidsCompute.SetInt("_NumCells", NumCells);
        CellBoidsCompute.SetInt("_NullCell", unchecked((int)NullCell));
        Debug.Log($"NullCell (uint) = {NullCell}, (int) = {unchecked((int)NullCell)}");

    }

    void SortBoidsByCell()
    {
        CellBoidsCompute.Dispatch(clearCellKernel, (NumCells + 15) / 16, 1, 1);
        RunRadixSort();
        CellBoidsCompute.Dispatch(buildRangesKernel, boidGroup, 1, 1);
        ValidateStartEnd();

    }

    void RunRadixSort()
    {

        DumpAssignedBoidCells();

        int bits = 9; //512 cells
        for (int shift = 0; shift < bits; shift += 4)
        {
            CellBoidsCompute.SetInt("_Shift", shift);

            CellBoidsCompute.Dispatch(radixClearKernel, 1, 1, 1);

            DebugCheckCleared();

            CellBoidsCompute.Dispatch(radixHistKernel, boidGroup, 1, 1);

            DebugPrintBucketCounts();

            CellBoidsCompute.Dispatch(radixPrefixKernel, 1, 1, 1);

            DebugCheckOffsets();



            CellBoidsCompute.SetBuffer(radixScatterKernel, "BoidCells", BoidCells);
            CellBoidsCompute.SetBuffer(radixScatterKernel, "BoidCellsTemp", BoidCellsTemp);

            CellBoidsCompute.Dispatch(radixScatterKernel, boidGroup, 1, 1);

            var temp = BoidCells;
            BoidCells = BoidCellsTemp;
            BoidCellsTemp = temp;

            DumpSortedBoidCells();

            CellBoidsCompute.SetBuffer(radixHistKernel, "BoidCells", BoidCells);
            CellBoidsCompute.SetBuffer(buildRangesKernel, "BoidCells", BoidCells);
        }
    }

    void PrintCellCounts()
    {
        uint[] start = new uint[NumCells];
        uint[] end = new uint[NumCells];

        CellStart.GetData(start);
        CellEnd.GetData(end);
        uint sum = 0;
        uint lookedAt = 0;
        for (int c = 0; c < NumCells; c++)
        {
            int x = c % 8;
            int y = c / 8 % 8;
            int z = c / 64;
            if (z != 0) continue;
            lookedAt++;
            if (start[c] == NullCell || end[c] == NullCell)
            {
                Debug.Log($"Cell [{x},{y},{z}]: 0 , actual: {count[x, y]}");
            }
            else
            {
                uint num = end[c] - start[c] + 1;
                sum += num;
                Debug.Log($"Cell [{x},{y},{z}]: {num}, actual: {count[x, y]}");
            }
        }
        Debug.Log($"TOTAL: {sum}, {lookedAt}");
    }

    void DebugPrintBucketCounts()
    {
        uint[] bc = new uint[16];
        BucketCounts.GetData(bc);
        uint sum = 0;
        for (int i = 0; i < bc.Length; i++)
        { //Debug.Log($"Bucket[{i}]={bc[i]}"); sum += bc[i]; 
        }
        log += $"Bucket sum = {sum}  (NumBoids = {NumBoids})" + "\n";
        //Debug.Log($"Bucket sum = {sum}  (NumBoids = {NumBoids})");
    }
    void ValidateStartEnd()
    {
        uint[] s = new uint[NumCells];
        uint[] e = new uint[NumCells];
        CellStart.GetData(s);
        CellEnd.GetData(e);
        for (int i = 0; i < NumCells; i++)
        {
            bool sNull = (s[i] == NullCell), eNull = (e[i] == NullCell);
            if (sNull ^ eNull) Debug.LogError($"Inconsistent start/end at {i}: start={s[i]}, end={e[i]}");
            if (!sNull && s[i] > e[i]) Debug.LogError($"start > end at {i}: start={s[i]} end={e[i]}");
        }
    }

    void DebugCheckCleared()
    {
        uint[] arr = new uint[16];
        BucketCounts.GetData(arr);
        for (int i = 0; i < 16; i++) if (arr[i] != 0) Debug.LogError($"BucketCounts not cleared at {i} = {arr[i]}");
        BucketOffsets.GetData(arr);
        for (int i = 0; i < 16; i++) if (arr[i] != 0) Debug.LogError($"BucketOffsets not cleared at {i} = {arr[i]}");
        BucketCounters.GetData(arr);
        for (int i = 0; i < 16; i++) if (arr[i] != 0) Debug.LogError($"BucketCounters not cleared at {i} = {arr[i]}");
    }

    void DebugCheckOffsets()
    {
        uint[] offsets = new uint[16];
        uint[] counts = new uint[16];
        BucketOffsets.GetData(offsets);
        BucketCounts.GetData(counts);
        for (int i = 0; i < 16; i++)
        {
            uint start = offsets[i];
            uint cnt = counts[i];
            log += $"off[{i}] = {start}, cnt[{i}] = {cnt}" + "\n";
            //Debug.Log($"off[{i}] = {start}, cnt[{i}] = {cnt}");
            if (start + cnt > (uint)NumBoids) Debug.LogError($"bucket {i} overflow: start+cnt = {start + cnt} > NumBoids");
        }


    }

    void DumpAssignedBoidCells(int N = 128)
    {
        var arr = new BoidCell[Mathf.Min(NumBoids, N)];
        // ensure you're reading buffer bound to assign kernel (BoidCells)
        BoidCells.GetData(arr, 0, 0, arr.Length);
        for (int i = 0; i < arr.Length; i++)
        {
            if (arr[i].cellIndex >= NumCells) Debug.LogError($"AssignCells BAD: boid {arr[i].boidIndex} cell {arr[i].cellIndex}");
        }
    }


    void DumpSortedBoidCells(int N = 64)
    {
        var arr = new BoidCell[Mathf.Min(NumBoids, N)];
        BoidCells.GetData(arr, 0, 0, arr.Length);
        for (int i = 0; i < arr.Length; i++)
        {
            log += $"sorted[{i}] boid {arr[i].boidIndex}, cell {arr[i].cellIndex}" + "\n";
            Debug.Log($"sorted[{i}] boid {arr[i].boidIndex}, cell {arr[i].cellIndex}");
            if (arr[i].cellIndex >= NumCells) Debug.LogError($"BAD cellIndex at sorted[{i}] = {arr[i].cellIndex}");
        }
    }

    void OnDestroy()
    {
        Boids?.Release();
        BoidCells?.Release();
        CellStart?.Release();
        CellEnd?.Release();
        BoidCellsTemp?.Release();
        BucketCounts?.Release();
        BucketOffsets?.Release();
        BucketCounters?.Release();

    }


}
