using UnityEngine;

public static class BoidParamsPresents
{
    public enum Present
    {
        Default,
        Custom
    }

    public static BoidParams GetPresent(Present p)
    {
        switch (p)
        {
            case Present.Default:
                return defaultParams;
        }
        Debug.LogWarning("requested unfound or no present, returning default");
        return defaultParams;

    }

    public static BoidParams defaultParams = new BoidParams
    {
        present = Present.Default,
        NeighborRadius = 1f / 8f,
        MinSpeed = 0.01f,
        MaxSpeed = .1f,
        SeperationDistance = 1f / 128f,
        SeperationWeight = 1f,
        AlignmentWeight = 1f,
        CohesionWeight = 1f
    };



}
