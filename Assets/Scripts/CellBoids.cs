using Unity.VisualScripting;
using UnityEngine;
using UnityEngine.Rendering;

public class CellBoids : MonoBehaviour
{
    [SerializeField] int NumBoids;
    [SerializeField][Range(0.01f, .5f)] float BoidRange;
    [SerializeField] ComputeShader CellBoidsCompute;

    private float WorldXY = 1f, WorldXYRange = 0.5f;
    private float CellSize;
    private int CellRowsCols, NumCells;

    private ComputeBuffer Boids;
    private ComputeBuffer BoidCells;
    private ComputeBuffer CellStart;
    private ComputeBuffer CellEnd;

    private const uint NullCell = 0xFFFFFFFF;

    private int updateKernel, assignCellKernel, clearCellKernel;

    private int boidThreads = 256, cellThreads = 16;

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


        CalculateCellSize();
        IntializeKernelIndicies();
        InitializeBuffers();
    }

    void Update()
    {
        int cellGroup = (NumCells + cellThreads - 1) / cellThreads;

        int boidGroup = (NumBoids + 256 - 1) / 256;
        CellBoidsCompute.Dispatch(assignCellKernel, boidGroup, 1, 1);

    


    }

    private void CalculateCellSize()
    {
        //smallest cellsize >= BoidRange 
        CellRowsCols = (int)(WorldXY / BoidRange);
        CellSize = (WorldXY - BoidRange * CellRowsCols) / CellRowsCols;
        NumCells = CellRowsCols * CellRowsCols;
    }

    private void IntializeKernelIndicies()
    {
        updateKernel = CellBoidsCompute.FindKernel("UpdateBoids");
        assignCellKernel = CellBoidsCompute.FindKernel("AssignCells");
        clearCellKernel = CellBoidsCompute.FindKernel("ClearCells");
    }

    private void InitializeBuffers()
    {
        //BOIDS
        Boids = new ComputeBuffer(NumBoids, sizeof(float) * 4);

        Boid[] boids = new Boid[NumBoids];
        for (int i = 0; i < NumBoids; i++)
        {
            boids[i].pos = new Vector2(Random.Range(-WorldXYRange, WorldXYRange), Random.Range(-WorldXYRange, WorldXYRange));
            boids[i].vel = Random.insideUnitCircle;
        }

        Boids.SetData(boids);


        CellBoidsCompute.SetBuffer(assignCellKernel, "AssignCells", Boids);

        //BOID CELLS
        BoidCells = new ComputeBuffer(NumBoids, sizeof(uint) * 2);

        BoidCell[] boidCells = new BoidCell[NumBoids];

        Boids.SetData(boidCells);

        CellBoidsCompute.SetBuffer(updateKernel, "BoidCells", BoidCells);
        CellBoidsCompute.SetBuffer(assignCellKernel, "BoidCells", BoidCells);

        //CELL START/END
        CellStart = new ComputeBuffer(CellRowsCols * CellRowsCols, sizeof(uint));
        CellEnd = new ComputeBuffer(CellRowsCols * CellRowsCols, sizeof(uint));

        uint[] cellStart = new uint[NumCells];
        uint[] cellEnd = new uint[NumCells];
        for (int i = 0; i < NumCells; i++)
        {
            cellStart[i] = NullCell;
            cellEnd[i] = NullCell;
        }

        CellStart.SetData(cellStart);
        CellEnd.SetData(cellEnd);

        CellBoidsCompute.SetBuffer(updateKernel, "CellStart", CellStart);
        CellBoidsCompute.SetBuffer(updateKernel, "CellEnd", CellEnd);
        CellBoidsCompute.SetBuffer(clearCellKernel, "CellStart", CellStart);
        CellBoidsCompute.SetBuffer(clearCellKernel, "CellEnd", CellEnd);
    }





    private void SetNonUpdatingParams()
    {
        CellBoidsCompute.SetInt("_NumBoids", NumBoids);

    }

    void OnDestroy()
    {
        if (Boids != null) Boids.Release();
        if (BoidCells != null) BoidCells.Release();
        if (CellStart != null) CellStart.Release();
        if (CellEnd != null) CellEnd.Release();

    }


}
