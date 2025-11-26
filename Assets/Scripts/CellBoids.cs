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
    private ComputeBuffer BlockCounts;
    private ComputeBuffer BlockOffsets;
    private int radix = 1 << 3;

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
        // Debug.Log(log);
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
        radixHistKernel = CellBoidsCompute.FindKernel("RadixBlockHistogram");
        radixPrefixKernel = CellBoidsCompute.FindKernel("RadixPrefixSum");
        radixScatterKernel = CellBoidsCompute.FindKernel("RadixScatterStable");
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

        BucketCounts = new ComputeBuffer(radix, sizeof(uint));
        BucketOffsets = new ComputeBuffer(radix, sizeof(uint));
        BucketCounters = new ComputeBuffer(radix, sizeof(uint));

        BlockCounts = new ComputeBuffer(boidGroup * radix, sizeof(uint));
        BlockOffsets = new ComputeBuffer(boidGroup * radix, sizeof(uint));

        //Buffer bindings

        // AssignCells uses Boids -> writes BoidCells
        CellBoidsCompute.SetBuffer(assignCellKernel, "Boids", Boids);
        CellBoidsCompute.SetBuffer(assignCellKernel, "BoidCells", BoidCells);

        CellBoidsCompute.SetBuffer(clearCellKernel, "CellStart", CellStart);
        CellBoidsCompute.SetBuffer(clearCellKernel, "CellEnd", CellEnd);

        CellBoidsCompute.SetBuffer(radixClearKernel, "BucketCounts", BucketCounts);
        CellBoidsCompute.SetBuffer(radixClearKernel, "BucketCounters", BucketCounters);
        CellBoidsCompute.SetBuffer(radixClearKernel, "BucketOffsets", BucketOffsets);

        CellBoidsCompute.SetBuffer(radixHistKernel, "BoidCells", BoidCells);
        CellBoidsCompute.SetBuffer(radixHistKernel, "BlockCounts", BlockCounts);
        CellBoidsCompute.SetBuffer(radixHistKernel, "BucketCounts", BucketCounts);

        CellBoidsCompute.SetBuffer(radixPrefixKernel, "BlockOffsets", BlockOffsets);
        CellBoidsCompute.SetBuffer(radixPrefixKernel, "BlockCounts", BlockCounts);

        CellBoidsCompute.SetBuffer(radixScatterKernel, "BucketOffsets", BucketOffsets);
        CellBoidsCompute.SetBuffer(radixScatterKernel, "BucketCounters", BucketCounters);
        CellBoidsCompute.SetBuffer(radixScatterKernel, "BoidCells", BoidCells);
        CellBoidsCompute.SetBuffer(radixScatterKernel, "BoidCellsTemp", BoidCellsTemp);
        CellBoidsCompute.SetBuffer(radixScatterKernel, "BlockOffsets", BlockOffsets);

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
        CellBoidsCompute.SetInt("_NumGroups", boidGroup);
        // Debug.Log($"NullCell (uint) = {NullCell}, (int) = {unchecked((int)NullCell)}");

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
        var inBuffer = BoidCells;
        var outBuffer = BoidCellsTemp;
        int bits = 9; //512 cells
        for (int shift = 0; shift < bits; shift += 3)
        {
            CellBoidsCompute.SetInt("_Shift", shift);

            RadixClear();
            ValidateRadixClear();

            // make sure histogram reads the current input buffer
            CellBoidsCompute.SetBuffer(radixHistKernel, "BoidCells", inBuffer);
            CellBoidsCompute.Dispatch(radixHistKernel, boidGroup, 1, 1);

            UpdateBlockOffsetsCounts();


            CellBoidsCompute.Dispatch(radixPrefixKernel, 1, 1, 1);
            ValidatePrefixOffsets();

            // scatter writes into the current output buffer
            CellBoidsCompute.SetBuffer(radixScatterKernel, "BoidCells", inBuffer);
            CellBoidsCompute.SetBuffer(radixScatterKernel, "BoidCellsTemp", outBuffer);
            CellBoidsCompute.Dispatch(radixScatterKernel, boidGroup, 1, 1);

            // swap the roles for next pass
            var temp = inBuffer;
            inBuffer = outBuffer;
            outBuffer = temp;

            PrintBoidCells($"post shift {shift}");
        }

        // Commit final buffers to fields and bind buildRanges to the final sorted buffer
        BoidCells = inBuffer;
        BoidCellsTemp = outBuffer;
        CellBoidsCompute.SetBuffer(buildRangesKernel, "BoidCells", BoidCells);

        VerifySortedAndUnique();
    }

    void RadixClear()
    {
        uint[] zeroArray = new uint[radix];
        uint[] zeroBlockArray = new uint[radix * boidGroup];
        BucketCounts.SetData(zeroArray);
        BucketOffsets.SetData(zeroArray);
        BucketCounters.SetData(zeroArray);
        BlockCounts.SetData(zeroBlockArray);
        BlockOffsets.SetData(zeroBlockArray);
    }

    void UpdateBlockOffsetsCounts()
    {
        uint[] blockCountsArr = new uint[boidGroup * radix];
        BlockCounts.GetData(blockCountsArr);

        uint[] blockOffsetsArr = new uint[boidGroup * radix];
        uint[] bucketCountsArr = new uint[radix];

        // For each bucket, scan groups in order
        for (int bucket = 0; bucket < radix; bucket++)
        {
            uint off = 0;
            uint total = 0;
            for (int group = 0; group < boidGroup; group++)
            {
                int idx = group * radix + bucket;
                blockOffsetsArr[idx] = off;
                off += blockCountsArr[idx];

                total += blockCountsArr[group * radix + bucket];
            }
            bucketCountsArr[bucket] = total;
        }

        BlockOffsets.SetData(blockOffsetsArr);
        BucketCounts.SetData(bucketCountsArr);
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

    void ValidatePrefixOffsets()
    {
        uint[] offs = new uint[radix];
        uint[] counts = new uint[radix];
        BucketOffsets.GetData(offs);
        BucketCounts.GetData(counts);
        uint sum = 0;
        string log = "";
        for (int i = 0; i < offs.Length; i++)
        {
            log += $"off[{i}]={offs[i]}, cnt[{i}]={counts[i]}\n";
            if (offs[i] + counts[i] > NumBoids) Debug.LogError($"BAD off+cnt > NumBoids at bucket {i}");
            sum += counts[i];
        }
        Debug.Log($"bucket sum check = {sum} (NumBoids={NumBoids})\n" + log);
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

    void PrintBoidCells(string when = "")
    {
        string log = $"BoidCells {(when == "" ? "" : "at " + when)}: \n";
        BoidCell[] bc = new BoidCell[NumBoids];
        BoidCells.GetData(bc);
        for (int i = 0; i < bc.Length; i++)
        {
            log += $"Boid ({bc[i].boidIndex}), Cell ({bc[i].cellIndex})\n";
        }

        Debug.Log(log);
    }

    void VerifySortedAndUnique()
    {
        // read full sorted BoidCells (the buffer currently bound as BoidCells in your C# after swaps)
        var arr = new BoidCell[NumBoids];
        BoidCells.GetData(arr);

        // 1) Check monotonic non-decreasing cellIndex
        int badIndex = -1;
        for (int i = 1; i < arr.Length; i++)
        {
            if (arr[i].cellIndex < arr[i - 1].cellIndex)
            {
                badIndex = i;
                break;
            }
        }

        if (badIndex >= 0)
        {
            string log = "";
            log += $"Context around error:\n";
            for (int k = Mathf.Max(0, badIndex - 8); k < Mathf.Min(arr.Length, badIndex + 8); k++)
                log += $"  pos {k}: boid {arr[k].boidIndex}, cell {arr[k].cellIndex}\n";

            Debug.LogError($"SORT ORDER VIOLATION at pos {badIndex - 1}..{badIndex}: cell[{badIndex - 1}]={arr[badIndex - 1].cellIndex} cell[{badIndex}]={arr[badIndex].cellIndex}\n " + log);
        }
        else
        {
            Debug.Log("Order check: OK (non-decreasing cellIndex).");
        }

        // 2) Check uniqueness / coverage of boidIndex 0..NumBoids-1
        var seen = new int[NumBoids];
        for (int i = 0; i < arr.Length; i++)
        {
            uint b = arr[i].boidIndex;
            if (b >= NumBoids) Debug.LogError($"BAD boidIndex value {b} at sorted pos {i}");
            else seen[b]++;
        }

        int missing = 0, dup = 0;
        for (int i = 0; i < NumBoids; i++)
        {
            if (seen[i] == 0) missing++;
            else if (seen[i] > 1) dup++;
        }
        Debug.Log($"Uniqueness: missing={missing}, duplicates={dup}");
        if (missing > 0 || dup > 0)
        {
            // print some examples
            for (int i = 0; i < Mathf.Min(NumBoids, 200); i++)
                if (seen[i] == 0) Debug.LogError($"Missing boid {i}");
                else if (seen[i] > 1) Debug.LogError($"Duplicated boid {i} count={seen[i]}");
        }
    }

    void ValidateRadixClear()
    {
        string log = "";
        uint[] offs = new uint[radix];
        uint[] counts = new uint[radix];
        uint[] counters = new uint[radix];
        uint[] blockoffs = new uint[radix];
        uint[] blockcounts = new uint[radix];
        BucketOffsets.GetData(offs);
        BucketCounts.GetData(counts);
        BucketCounters.GetData(counters);
        for (int i = 0; i < offs.Length; i++)
        {
            if (offs[i] != 0) log += $"Offset[{i}] not cleared! Value = {offs[i]} \n";
            if (counts[i] != 0) log += $"Counts[{i}] not cleared! Value = {counts[i]}\n";
            if (counters[i] != 0) log += $"counters[{i}] not cleared! Value = {counters[i]}\n";
            if (blockoffs[i] != 0) log += $"BlockOffset[{i}] not cleared! Value = {blockoffs[i]} \n";
            if (blockcounts[i] != 0) log += $"BlockCounts[{i}] not cleared! Value = {blockcounts[i]}\n";
        }
        if (log != "") Debug.LogWarning("Radix was not cleared!\n" + log);

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
        BlockCounts?.Release();
        BlockOffsets?.Release();

    }


}
