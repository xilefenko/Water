// Till Dawn Linux compatibility presentation path.
// Identical simulation sampling and NEW_LIGHTING shading as Custom/FFTWater, but the
// hull/domain/geometry stages are replaced by a plain vertex stage fed by a dense CPU mesh.
// The original Custom/FFTWater shader remains the untouched D3D11 control.
Shader "Custom/FFTWaterCompat" {

	Properties {
		[Enum(Off, 0, On, 1)] _ZWrite ("Z Write", Float) = 1
	}

	SubShader {
		Tags {
			"LightMode" = "ForwardBase"
		}

		Pass {

			CGPROGRAM

			#pragma vertex vp
			#pragma fragment fp
			#pragma target 4.0

			#include "UnityPBSLighting.cginc"
			#include "AutoLight.cginc"

			#define PI 3.14159265358979323846

			struct VertexData {
				float4 vertex : POSITION;
				float2 uv : TEXCOORD0;
			};

			struct v2f {
				float4 pos : SV_POSITION;
				float2 uv : TEXCOORD0;
				float3 worldPos : TEXCOORD1;
				float depth : TEXCOORD2;
			};

			float3 _SunDirection, _SunColor;

			float _NormalStrength, _FresnelNormalStrength, _SpecularNormalStrength;

			samplerCUBE _EnvironmentMap;
			int _UseEnvironmentMap;

			float3 _Ambient, _DiffuseReflectance, _SpecularReflectance, _FresnelColor, _TipColor;
			float _Shininess, _FresnelBias, _FresnelStrength, _FresnelShininess, _TipAttenuation;
			float _Roughness, _FoamRoughnessModifier;
			float _Tile0, _Tile1, _Tile2, _Tile3;
			float3 _SunIrradiance, _ScatterColor, _BubbleColor, _FoamColor;
			float _HeightModifier, _BubbleDensity;
			float _DisplacementDepthAttenuation, _FoamDepthAttenuation, _NormalDepthAttenuation;
			float _WavePeakScatterStrength, _ScatterStrength, _ScatterShadowStrength, _EnvironmentLightStrength;

			int _DebugTile0, _DebugTile1, _DebugTile2, _DebugTile3;
			int _ContributeDisplacement0, _ContributeDisplacement1, _ContributeDisplacement2, _ContributeDisplacement3;
			int _DebugLayer0, _DebugLayer1, _DebugLayer2, _DebugLayer3;
			float _FoamSubtract0, _FoamSubtract1, _FoamSubtract2, _FoamSubtract3;

			// 0 = full NEW_LIGHTING composition (parity target)
			// 1 = flat diagnostic geometry (headlight lambert, checkpoint 1)
			// 2 = displacement visualization  3 = normals  4 = foam only
			// 5 = specular only  6 = fresnel/environment only  7 = scatter only
			// 8..11 = per-band height, 12 = horizontal displacement, 13 = height,
			// 14 = raw foam history, 15 = Jacobian/fold proxy (the retained foam source).
			int _CompatDebugMode;

			float4x4 _CameraInvViewProjection;
			sampler2D _CameraDepthTexture;
			UNITY_DECLARE_TEX2DARRAY(_DisplacementTextures);
			UNITY_DECLARE_TEX2DARRAY(_SlopeTextures);
			UNITY_DECLARE_TEX2DARRAY(_JacobianTextures);

			v2f vp(VertexData v) {
				v2f g;
				g.worldPos = mul(unity_ObjectToWorld, v.vertex);

				float3 displacement1 = UNITY_SAMPLE_TEX2DARRAY_LOD(_DisplacementTextures, float3(g.worldPos.xz * _Tile0, 0), 0) * _DebugLayer0 * _ContributeDisplacement0;
				float3 displacement2 = UNITY_SAMPLE_TEX2DARRAY_LOD(_DisplacementTextures, float3(g.worldPos.xz * _Tile1, 1), 0) * _DebugLayer1 * _ContributeDisplacement1;
				float3 displacement3 = UNITY_SAMPLE_TEX2DARRAY_LOD(_DisplacementTextures, float3(g.worldPos.xz * _Tile2, 2), 0) * _DebugLayer2 * _ContributeDisplacement2;
				float3 displacement4 = UNITY_SAMPLE_TEX2DARRAY_LOD(_DisplacementTextures, float3(g.worldPos.xz * _Tile3, 3), 0) * _DebugLayer3 * _ContributeDisplacement3;
				float3 displacement = displacement1 + displacement2 + displacement3 + displacement4;

				float4 clipPos = UnityObjectToClipPos(v.vertex);
				float depth = 1 - Linear01Depth(clipPos.z / clipPos.w);

				displacement = lerp(0.0f, displacement, pow(saturate(depth), _DisplacementDepthAttenuation));

				v.vertex.xyz += mul(unity_WorldToObject, displacement.xyz);

				g.pos = UnityObjectToClipPos(v.vertex);
				g.uv = g.worldPos.xz;
				g.worldPos = mul(unity_ObjectToWorld, v.vertex);
				g.depth = depth;
				return g;
			}

			float SchlickFresnel(float3 normal, float3 viewDir) {
				return 0.02f + (1 - 0.02f) * (pow(1 - DotClamped(normal, viewDir), 5.0f));
			}

			float SmithMaskingBeckmann(float3 H, float3 S, float roughness) {
				float hdots = max(0.001f, DotClamped(H, S));
				float a = hdots / (roughness * sqrt(1 - hdots * hdots));
				float a2 = a * a;

				return a < 1.6f ? (1.0f - 1.259f * a + 0.396f * a2) / (3.535f * a + 2.181 * a2) : 0.0f;
			}

			float Beckmann(float ndoth, float roughness) {
				float exp_arg = (ndoth * ndoth - 1) / (roughness * roughness * ndoth * ndoth);

				return exp(exp_arg) / (PI * roughness * roughness * ndoth * ndoth * ndoth * ndoth);
			}

			float4 fp(v2f f) : SV_TARGET {
				float3 lightDir = -normalize(_SunDirection);
				float3 viewDir = normalize(_WorldSpaceCameraPos - f.worldPos);
				float3 halfwayDir = normalize(lightDir + viewDir);
				float depth = f.depth;
				float LdotH = DotClamped(lightDir, halfwayDir);
				float VdotH = DotClamped(viewDir, halfwayDir);

				float4 displacementFoam1 = UNITY_SAMPLE_TEX2DARRAY(_DisplacementTextures, float3(f.uv * _Tile0, 0)) * _DebugLayer0;
				displacementFoam1.a += _FoamSubtract0;
				float4 displacementFoam2 = UNITY_SAMPLE_TEX2DARRAY(_DisplacementTextures, float3(f.uv * _Tile1, 1)) * _DebugLayer1;
				displacementFoam2.a += _FoamSubtract1;
				float4 displacementFoam3 = UNITY_SAMPLE_TEX2DARRAY(_DisplacementTextures, float3(f.uv * _Tile2, 2)) * _DebugLayer2;
				displacementFoam3.a += _FoamSubtract2;
				float4 displacementFoam4 = UNITY_SAMPLE_TEX2DARRAY(_DisplacementTextures, float3(f.uv * _Tile3, 3)) * _DebugLayer3;
				displacementFoam4.a += _FoamSubtract3;
				float4 displacementFoam = displacementFoam1 + displacementFoam2 + displacementFoam3 + displacementFoam4;

				float2 slopes1 = UNITY_SAMPLE_TEX2DARRAY(_SlopeTextures, float3(f.uv * _Tile0, 0)) * _DebugLayer0;
				float2 slopes2 = UNITY_SAMPLE_TEX2DARRAY(_SlopeTextures, float3(f.uv * _Tile1, 1)) * _DebugLayer1;
				float2 slopes3 = UNITY_SAMPLE_TEX2DARRAY(_SlopeTextures, float3(f.uv * _Tile2, 2)) * _DebugLayer2;
				float2 slopes4 = UNITY_SAMPLE_TEX2DARRAY(_SlopeTextures, float3(f.uv * _Tile3, 3)) * _DebugLayer3;
				float2 slopes = slopes1 + slopes2 + slopes3 + slopes4;

				slopes *= _NormalStrength;
				float foam = lerp(0.0f, saturate(displacementFoam.a), pow(depth, _FoamDepthAttenuation));

				float3 macroNormal = float3(0, 1, 0);
				float3 mesoNormal = normalize(float3(-slopes.x, 1.0f, -slopes.y));
				mesoNormal = normalize(lerp(float3(0, 1, 0), mesoNormal, pow(saturate(depth), _NormalDepthAttenuation)));
				mesoNormal = normalize(UnityObjectToWorldNormal(normalize(mesoNormal)));

				float NdotL = DotClamped(mesoNormal, lightDir);

				float a = _Roughness + foam * _FoamRoughnessModifier;
				float ndoth = max(0.0001f, dot(mesoNormal, halfwayDir));

				float viewMask = SmithMaskingBeckmann(halfwayDir, viewDir, a);
				float lightMask = SmithMaskingBeckmann(halfwayDir, lightDir, a);

				float G = rcp(1 + viewMask + lightMask);

				float eta = 1.33f;
				float R = ((eta - 1) * (eta - 1)) / ((eta + 1) * (eta + 1));
				float thetaV = acos(viewDir.y);

				float numerator = pow(1 - dot(mesoNormal, viewDir), 5 * exp(-2.69 * a));
				float F = R + (1 - R) * numerator / (1.0f + 22.7f * pow(a, 1.5f));
				F = saturate(F);

				float3 specular = _SunIrradiance * F * G * Beckmann(ndoth, a);
				specular /= 4.0f * max(0.001f, DotClamped(macroNormal, lightDir));
				specular *= DotClamped(mesoNormal, lightDir);

				float3 envReflection = texCUBE(_EnvironmentMap, reflect(-viewDir, mesoNormal)).rgb;
				envReflection *= _EnvironmentLightStrength;

				float H = max(0.0f, displacementFoam.y) * _HeightModifier;
				float3 scatterColor = _ScatterColor;
				float3 bubbleColor = _BubbleColor;
				float bubbleDensity = _BubbleDensity;

				float k1 = _WavePeakScatterStrength * H * pow(DotClamped(lightDir, -viewDir), 4.0f) * pow(0.5f - 0.5f * dot(lightDir, mesoNormal), 3.0f);
				float k2 = _ScatterStrength * pow(DotClamped(viewDir, mesoNormal), 2.0f);
				float k3 = _ScatterShadowStrength * NdotL;
				float k4 = bubbleDensity;

				float3 scatter = (k1 + k2) * scatterColor * _SunIrradiance * rcp(1 + lightMask);
				scatter += k3 * scatterColor * _SunIrradiance + k4 * bubbleColor * _SunIrradiance;

				float3 output = (1 - F) * scatter + specular + F * envReflection;
				output = max(0.0f, output);
				output = lerp(output, _FoamColor, saturate(foam));

				if (_CompatDebugMode == 1) {
					// Checkpoint 1: geometry-only view. Headlight lambert plus height tint so
					// holes, static patches, and jitter grids are unmistakable.
					float headlight = DotClamped(mesoNormal, viewDir);
					float heightTint = saturate(displacementFoam.y * 0.25f + 0.5f);
					output = float3(headlight * 0.8f, headlight * (0.5f + 0.5f * heightTint), heightTint);
				}
				if (_CompatDebugMode == 2) output = float3(displacementFoam.x, displacementFoam.y, displacementFoam.z) * 0.25f + 0.5f;
				if (_CompatDebugMode == 3) output = mesoNormal * 0.5f + 0.5f;
				if (_CompatDebugMode == 4) output = saturate(foam);
				if (_CompatDebugMode == 5) output = specular;
				if (_CompatDebugMode == 6) output = F * envReflection;
				if (_CompatDebugMode == 7) output = (1 - F) * scatter;
				if (_CompatDebugMode >= 8 && _CompatDebugMode <= 11) {
					int band = _CompatDebugMode - 8;
					float tile = band == 0 ? _Tile0 : (band == 1 ? _Tile1 : (band == 2 ? _Tile2 : _Tile3));
					float height = UNITY_SAMPLE_TEX2DARRAY(_DisplacementTextures, float3(f.uv * tile, band)).y;
					output = height * 0.25f + 0.5f;
				}
				if (_CompatDebugMode == 12) output = float3(displacementFoam.x, 0, displacementFoam.z) * 0.25f + 0.5f;
				if (_CompatDebugMode == 13) output = displacementFoam.y * 0.25f + 0.5f;
				if (_CompatDebugMode == 14) output = saturate(displacementFoam.a);
				if (_CompatDebugMode == 15) {
					float jacobian = UNITY_SAMPLE_TEX2DARRAY(_JacobianTextures, float3(f.uv * _Tile0, 0));
					jacobian += UNITY_SAMPLE_TEX2DARRAY(_JacobianTextures, float3(f.uv * _Tile1, 1));
					jacobian += UNITY_SAMPLE_TEX2DARRAY(_JacobianTextures, float3(f.uv * _Tile2, 2));
					jacobian += UNITY_SAMPLE_TEX2DARRAY(_JacobianTextures, float3(f.uv * _Tile3, 3));
					output = saturate((jacobian - 4.0f) * 0.25f + 0.5f);
				}

				if (_DebugTile0) {
					output = cos(f.uv.x * _Tile0 * PI) * cos(f.uv.y * _Tile0 * PI);
				}

				if (_DebugTile1) {
					output = cos(f.uv.x * _Tile1) * 1024 * cos(f.uv.y * _Tile1) * 1024;
				}

				if (_DebugTile2) {
					output = cos(f.uv.x * _Tile2) * 1024 * cos(f.uv.y * _Tile2) * 1024;
				}

				if (_DebugTile3) {
					output = cos(f.uv.x * _Tile3) * 1024 * cos(f.uv.y * _Tile3) * 1024;
				}

				return float4(output, 1.0f);
			}

			ENDCG
		}
	}
}
