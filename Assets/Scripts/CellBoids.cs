using Unity.VisualScripting;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

public class CellBoids : MonoBehaviour
{
    [SerializeField] int NumBoids = 100;
    [SerializeField][Range(0.01f, .5f)] float BoidRange;

    [SerializeField] ComputeShader CellBoidsCompute;
    [SerializeField] Material BoidMaterial;
    [SerializeField] BoidParams boidParams = BoidParamsPresents.defaultParams;
    private BoidParams lastboidParams;
    private bool paramsChanged;
    [SerializeField] private bool debugRadix = false;

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
    uint[] zeroArray;
    uint[] zeroBlockArray;

    private const uint NullCell = 0xFFFFFFFF;

    private int updateKernel, assignCellKernel, clearCellKernel, radixClearKernel, radixHistKernel, radixPrefixKernel, radixScatterKernel, buildRangesKernel;

    private int boidThreads = 256, cellThreads = 16;
    private int boidGroup;

    private string log;
    uint[,] count;


    [SerializeField] float boidSize = 1f;
    [SerializeField] Color slowColor, fastColor;
    private Mesh boidMesh;

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

        boidMesh = CreateQuad();

        BoidMaterial.SetBuffer("_Boids", Boids);
        BoidMaterial.SetFloat("_MinSpeed", boidParams.MinSpeed);
        BoidMaterial.SetFloat("_MaxSpeed", boidParams.MaxSpeed);

        IntializeKernelIndicies();
        InitializeBuffers();
        SetNonUpdatingParams();
        SetBoidParams();
    }

    void Update()
    {
        SortBoidsByCell();
        BoidsUpdate();
    }

    void OnValidate()
    {
        if (!boidParams.Equals(lastboidParams))
        {
            paramsChanged = true;
            if (boidParams.present == lastboidParams.present)
            {
                boidParams.present = BoidParamsPresents.Present.Custom;
            }
            else if (boidParams.present != BoidParamsPresents.Present.Custom)
            {
                boidParams = BoidParamsPresents.GetPresent(boidParams.present);
            }
            lastboidParams = boidParams;
        }
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

        zeroArray = new uint[radix];
        zeroBlockArray = new uint[radix * boidGroup];

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

        CellBoidsCompute.SetBuffer(updateKernel, "Boids", Boids);
        CellBoidsCompute.SetBuffer(updateKernel, "BoidCells", BoidCells);
        CellBoidsCompute.SetBuffer(updateKernel, "CellStart", CellStart);
        CellBoidsCompute.SetBuffer(updateKernel, "CellEnd", CellEnd);


    }

    private void SetNonUpdatingParams()
    {
        CellBoidsCompute.SetInt("_NumBoids", NumBoids);
        CellBoidsCompute.SetFloat("_CellSize", CellSize);
        CellBoidsCompute.SetInt("_CellSeats", CellSeats);
        CellBoidsCompute.SetInt("_NumCells", NumCells);
        CellBoidsCompute.SetInt("_NullCell", unchecked((int)NullCell));
        CellBoidsCompute.SetInt("_NumGroups", boidGroup);
    }

    private void SetBoidParams()
    {
        paramsChanged = false;
        CellBoidsCompute.SetFloat("_NeighborRadius", boidParams.NeighborRadius);
        CellBoidsCompute.SetFloat("_SeparationDistance", boidParams.SeperationDistance);
        CellBoidsCompute.SetFloat("_SeparationWeight", boidParams.SeperationWeight);
        CellBoidsCompute.SetFloat("_AlignmentWeight", boidParams.AlignmentWeight);
        CellBoidsCompute.SetFloat("_CohesionWeight", boidParams.CohesionWeight);
        CellBoidsCompute.SetFloat("_MaxSpeed", boidParams.MaxSpeed);
        CellBoidsCompute.SetFloat("_MinSpeed", boidParams.MinSpeed);
    }

    void BoidsUpdate()
    {
        if (paramsChanged) SetBoidParams();
        CellBoidsCompute.SetBuffer(updateKernel, "BoidCells", BoidCells);
        CellBoidsCompute.SetBuffer(updateKernel, "CellStart", CellStart); //may not be necessary
        CellBoidsCompute.SetBuffer(updateKernel, "CellEnd", CellEnd); //may not be necessary
        CellBoidsCompute.SetFloat("_DeltaTime", Time.deltaTime);
        CellBoidsCompute.Dispatch(updateKernel, boidGroup, 1, 1);
    }

    void SortBoidsByCell()
    {
        CellBoidsCompute.Dispatch(assignCellKernel, boidGroup, 1, 1);
        CellBoidsCompute.Dispatch(clearCellKernel, (NumCells + cellThreads - 1) / cellThreads, 1, 1);
        RunRadixSort();
        CellBoidsCompute.Dispatch(buildRangesKernel, boidGroup, 1, 1);
        if (debugRadix) ValidateStartEnd();
        if (debugRadix) VerifyCellRangesFull();

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
            if (debugRadix) ValidateRadixClear();

            CellBoidsCompute.SetBuffer(radixHistKernel, "BoidCells", inBuffer);
            CellBoidsCompute.Dispatch(radixHistKernel, boidGroup, 1, 1);

            UpdateBlockOffsetsCounts();

            CellBoidsCompute.SetBuffer(radixScatterKernel, "BoidCells", inBuffer);
            CellBoidsCompute.SetBuffer(radixScatterKernel, "BoidCellsTemp", outBuffer);
            CellBoidsCompute.Dispatch(radixScatterKernel, boidGroup, 1, 1);

            var temp = inBuffer;
            inBuffer = outBuffer;
            outBuffer = temp;

            if (debugRadix) PrintBoidCells($"post shift {shift}");
        }

        BoidCells = inBuffer;
        BoidCellsTemp = outBuffer;
        CellBoidsCompute.SetBuffer(buildRangesKernel, "BoidCells", BoidCells);

        if (debugRadix) VerifySortedAndUnique();
    }

    void RadixClear()
    {

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

        uint globalOffset = 0;

        for (int bucket = 0; bucket < radix; bucket++)
        {
            uint bucketTotal = 0;

            for (int group = 0; group < boidGroup; group++)
            {
                int idx = group * radix + bucket;

                blockOffsetsArr[idx] = globalOffset;

                uint count = blockCountsArr[idx];
                globalOffset += count;
                bucketTotal += count;
            }
            bucketCountsArr[bucket] = bucketTotal;
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

    void VerifyCellRangesFull()
    {
        uint[] starts = new uint[NumCells];
        uint[] ends = new uint[NumCells];
        BoidCell[] sortedBoids = new BoidCell[NumBoids];

        // Read back all necessary buffers
        CellStart.GetData(starts);
        CellEnd.GetData(ends);
        BoidCells.GetData(sortedBoids); // Ensure this is the FINAL sorted buffer

        int errorCount = 0;

        for (int cellId = 0; cellId < NumCells; cellId++)
        {
            uint s = starts[cellId];
            uint e = ends[cellId];

            // 1. Check consistency of Empty Cells
            if (s == NullCell)
            {
                if (e != NullCell)
                {
                    Debug.LogError($"Cell {cellId}: Start is NULL but End is {e}");
                    errorCount++;
                }
                continue; // Correctly empty
            }

            // 2. Check Range Logic
            if (e == NullCell)
            {
                Debug.LogError($"Cell {cellId}: Start is {s} but End is NULL");
                errorCount++;
                continue;
            }
            if (s > e)
            {
                Debug.LogError($"Cell {cellId}: Inverted Range! Start {s} > End {e}");
                errorCount++;
                continue;
            }
            if (e >= NumBoids)
            {
                Debug.LogError($"Cell {cellId}: End {e} is out of bounds (NumBoids={NumBoids})");
                errorCount++;
                continue;
            }

            // 3. Verify Content (The most important part)
            // Check that the boid at 'Start' actually belongs to this cell
            if (sortedBoids[s].cellIndex != cellId)
            {
                Debug.LogError($"Cell {cellId}: Start index {s} points to Boid with WRONG CellIndex {sortedBoids[s].cellIndex}");
                errorCount++;
            }

            // Check that the boid at 'End' actually belongs to this cell
            if (sortedBoids[e].cellIndex != cellId)
            {
                Debug.LogError($"Cell {cellId}: End index {e} points to Boid with WRONG CellIndex {sortedBoids[e].cellIndex}");
                errorCount++;
            }

            // 4. Verify Tightness (Boundary Checks)
            // Ensure we didn't start too late: The boid immediately BEFORE start must NOT be this cell
            if (s > 0)
            {
                if (sortedBoids[s - 1].cellIndex == cellId)
                {
                    Debug.LogError($"Cell {cellId}: Range starts too late! Boid at {s - 1} also belongs to this cell.");
                    errorCount++;
                }
            }

            // Ensure we didn't end too early: The boid immediately AFTER end must NOT be this cell
            if (e < NumBoids - 1)
            {
                if (sortedBoids[e + 1].cellIndex == cellId)
                {
                    Debug.LogError($"Cell {cellId}: Range ends too early! Boid at {e + 1} also belongs to this cell.");
                    errorCount++;
                }
            }
        }

        if (errorCount == 0)
        {
            Debug.Log("Range Verification Passed: All ranges match sorted data.");
        }
        else
        {
            Debug.LogError($"Range Verification FAILED with {errorCount} errors.");
        }
    }

    void OnRenderObject()
    {
        if (BoidMaterial == null || Boids == null || boidMesh == null || Camera.current == null)
        {
            return;
        }


        if (Camera.current.cameraType != CameraType.Game && Camera.current.cameraType != CameraType.SceneView)
        {
            return;
        }

        BoidMaterial.SetBuffer("_Boids", Boids);
        BoidMaterial.SetFloat("_MinSpeed", boidParams.MinSpeed);
        BoidMaterial.SetFloat("_MaxSpeed", boidParams.MaxSpeed);

        BoidMaterial.SetColor("_Color1", slowColor);
        BoidMaterial.SetColor("_Color2", fastColor);

        Bounds bounds = new Bounds(Vector3.zero, Vector3.one * 20f);

        BoidMaterial.SetPass(0);

        Graphics.DrawMeshInstancedProcedural(
            boidMesh,
            0,
            BoidMaterial,
            bounds,
            NumBoids
        );
    }

    private Mesh CreateQuad()
    {
        Mesh mesh = new Mesh();
        Vector3[] vertices = new Vector3[]
        {
            new Vector3(-0.01f * boidSize, -0.01f * boidSize, 0),
            new Vector3(0.01f * boidSize, -0.01f * boidSize, 0),
            new Vector3(-0.01f * boidSize, 0.01f * boidSize, 0),
            new Vector3(0.01f * boidSize, 0.01f * boidSize, 0)
        };

        int[] tris = new int[]
        {
            0, 2, 1,
            2, 3, 1
        };

        mesh.vertices = vertices;
        mesh.triangles = tris;
        mesh.RecalculateBounds();

        return mesh;
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
