# Edge-based Variable Rate Shading

This repo is an unofficial extension to the Unity sample for Variable Rate Shading: https://github.com/Unity-Technologies/shading-rate-demo

You can import this asset into the VRS sample project, and apply a shading rate reduction using edge detection.
<p align="center">
  <img width="100%" src="https://github.com/eldnach/vrs-edge/blob/main/.github/images/vrs_edge.gif?raw=true" alt="vrs demo">
</p>

This renderer feature uses the Sentis API to import and execute a Sobel Edge Detection model. The output of the model will be converted to a shading rate image, which can be used to optimize the performance of render passes.

Setting a uniform (screen-wide) 2x2 shading rate results in visual artifacts when zoomed:
<p align="center">
  <img width="70%" src="https://github.com/eldnach/vrs-edge/blob/main/.github/images/2x2_vrs.png/?raw=true" alt="2x2 shading rate">
</p>

By applying edge detection to the shading rate, visual fidelity is greatly improved:
<p align="center">
  <img width="70%" src="https://github.com/eldnach/vrs-edge/blob/main/.github/images/edge_vrs.png?raw=true" alt="edge based shading rate">
</p>

Measuring GPU timing of a Volumetric Lighting pass using VRS:
| Shading Rate  | GPU Time (Volumetrics)|
| ------------- |:---------------------:|
| Uniform 1x1   | ~6.4 ms               |
| Uniform 2x2   | ~1.6 ms (75% faster)  |  
| Uniform 4x4   | ~0.5 ms (92% faster)  | 
| Edge based    | ~4.1 ms (35% faster)  | 

The Sentis Sobel filter is using a 256x256 input tensor, and is executing at around ~0.22ms:
<p align="center">
  <img width="100%" src="https://github.com/eldnach/vrs-edge/blob/main/.github/images/sentis.png?raw=true" alt="sentis sobel">
</p>

## Usage

Import the files to your project's Asset folder. Navigate to your active render pipeline asset and click on `Add Renderer Feature`. Select the `VRSEdge` feature to enable VRS generation.

By default, a VRS generation pass will inject at the `After Rendering Pre Passes` event. This allows the Sobel filter to access the URP depth buffer. You may want to modify the Injection Point property based on your project's `Depth Texture Mode` setting.

<p align="left">
  <img width="70%" src="https://github.com/eldnach/vrs-edge/blob/main/.github/images/renderer-feature.png?raw=true" alt="renderer feature">
</p>

We can verify that VRS generation works as intended by opening `Window-> Analysis-> Render Graph Viewer`, and identifing the `Sentis Sobel` and `Shading Rate From Edge` passes:

<p align="left">
  <img width="70%" src="https://github.com/eldnach/vrs-edge/blob/main/.github/images/rgviewer.png?raw=true" alt="rendergrpah viewer">
</p>

The genereated shading rate image can now be applied to your project's render passes. Before doing so, first query for VRS support using `ShadingRateInfo.supportsPerImageTile`.

Reference the shading rate image using `frameData.Get<VRSEdge.VRSData>()`. Lastly, call `builder.SetShadingRateImageAttachment(vrsData.sri)` to apply VRS on your render pass.

```
public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
{
    UniversalResourceData resourcesData = frameData.Get<UniversalResourceData>();

    using (var builder = renderGraph.AddRasterRenderPass<MainPassData>("My Render Pass", out var passData, profilingSampler))
    {
        passData.material = m_Material;
        builder.SetRenderAttachment(resourcesData.activeColorTexture, 0, AccessFlags.Write);

        if(ShadingRateInfo.supportsPerImageTile){
            var vrsData = frameData.Get<VRSEdge.VRSData>();
            if (vrsData.sri.IsValid())
            {
                builder.SetShadingRateImageAttachment(vrsData.sri);
                builder.SetShadingRateCombiner(ShadingRateCombinerStage.Fragment,
                ShadingRateCombiner.Override);
            }
        }

        builder.SetRenderFunc((MainPassData data, RasterGraphContext context) =>
        {
            ExecuteMainPass(data, context);
        });
    }
}
```

## Requirements

- Unity 6.1
- Sentis 2.1.1 (can be imported from the Unity Package Manager)

## Platform Support

- Android devices with support for Vulkan Fragment Shading Rate
- Windows devices with support for DirectX12 Variable Rate Shading 
- Compatible consoles
