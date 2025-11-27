Shader "Unlit/BoidRenderInstanced"
{
    Properties
    {
        _Color1("Color 1", Color) = (0, 0, 1, 1)
        _Color2("Color 2", Color) = (1, 0, 0, 1)
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "DisableBatching" = "True" "Queue" = "Transparent" }

        Pass
        {
            ZTest Always
            ZWrite Off
            Cull Off

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.5
            #include "UnityCG.cginc"

            struct Boid
            {
                float2 pos;
                float2 vel;
            };

            StructuredBuffer<Boid> _Boids;

            float4 _Color1;
            float4 _Color2;
            float _MinSpeed;
            float _MaxSpeed;

            struct appdata
            {
                float3 vertex : POSITION;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float4 col : COLOR;
            };

            v2f vert(appdata v, uint instanceID : SV_InstanceID)
            {
                v2f o;

                Boid b = _Boids[instanceID];

                // Compute rotation direction based on velocity
                float angle = atan2(b.vel.y, b.vel.x);
                float s = sin(angle);
                float c = cos(angle);

                float4x4 model = float4x4(
                    c, -s, 0, b.pos.x,
                    s,  c, 0, b.pos.y,
                    0,  0, 1, 0,
                    0,  0, 0, 1
                );

                float4 worldPos = mul(model, float4(v.vertex, 1.0));
                o.pos = UnityWorldToClipPos(worldPos.xyz);

                // Get boid speed
                float speed = length(b.vel);

                // Normalize speed between 0–1 based on min/max range
                float t = saturate((speed - _MinSpeed) / (_MaxSpeed - _MinSpeed));

                // Interpolate between colors based on normalized speed
                o.col = lerp(_Color1, _Color2, t);

                return o;
            }

            half4 frag(v2f i) : SV_Target
            {
                return i.col;
            }
            ENDCG
        }
    }
}
