using UnityEngine;

[System.Serializable]
public struct BoidParams
{
    public BoidParamsPresents.Present present;
    [Range(0.01f, .5f)] public float NeighborRadius;
    [Range(0.01f, 1f)] public float MinSpeed;
    [Range(0.01f, 1f)] public float MaxSpeed;
    [Range(0.0001f, 0.01f)] public float SeperationDistance;
    [Range(0f, 10f)] public float SeperationWeight;
    [Range(0f, 10f)] public float CohesionWeight;
    [Range(0f, 10f)] public float AlignmentWeight;

    public bool Equals(BoidParams other)
    {
        bool ret =
            NeighborRadius == other.NeighborRadius &&
            MinSpeed == other.MinSpeed &&
            MaxSpeed == other.MaxSpeed &&
            SeperationDistance == other.SeperationDistance &&
            SeperationWeight == other.SeperationWeight &&
            CohesionWeight == other.CohesionWeight &&
            AlignmentWeight == other.AlignmentWeight &&
            present == other.present;
            
        return ret;
    }
}
