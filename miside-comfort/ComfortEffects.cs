using System;
using System.Collections.Generic;
using HarmonyLib;
using Il2CppInterop.Runtime;
using MiSide_VR.Core;
using UnityEngine;
using UnityEngine.Rendering.PostProcessing;
namespace Codex.MiSideComfort;
internal static class ComfortEffects {
 static Camera cachedCamera;
 static PostProcessLayer settingsLayer, temporarilyEnabled;
 static Vignette vignette;
 static readonly List<Behaviour> suppressed=new();
 static readonly List<PostProcessEffectSettings> suppressedSettings=new();
 static bool failed,missingReported,maskRendered;
 static int lastRefresh=-100;
 internal static void Install(Harmony h){
  var mirror=AccessTools.TypeByName("MiSide_VR.Core.Rendering.CameraEffectMirror");
  h.Patch(AccessTools.Method(mirror,"Synchronize"),postfix:new HarmonyMethod(typeof(ComfortEffects),nameof(AfterMirror)));
  // Override only the HEADSET layer's blended per-camera state. No global volume/shared assets.
  h.Patch(AccessTools.Method(typeof(PostProcessManager),"UpdateSettings",new[]{typeof(PostProcessLayer),typeof(Camera)}),postfix:new HarmonyMethod(typeof(ComfortEffects),nameof(AfterVolumeBlend)));
 }
 static T Setting<T>(PostProcessLayer layer) where T:PostProcessEffectSettings{return layer.GetBundle(Il2CppType.Of<T>()).settings.Cast<T>();}
 static void AfterVolumeBlend(PostProcessLayer __0,Camera __1){
  var p=VRPlayer.Instance;
  if(failed||!p||__1!=p.headCamera||!__0)return;
  try{
   if(settingsLayer!=__0){
    settingsLayer=__0;vignette=Setting<Vignette>(__0);suppressedSettings.Clear();
    suppressedSettings.Add(Setting<MotionBlur>(__0));suppressedSettings.Add(Setting<DepthOfField>(__0));
    suppressedSettings.Add(Setting<ChromaticAberration>(__0));suppressedSettings.Add(Setting<Grain>(__0));suppressedSettings.Add(Setting<LensDistortion>(__0));
    Plugin.Logger.LogInfo("Headset-only post-process settings bound; no global volume created.");
   }
   if(Settings.ReduceEffects.Value)foreach(var setting in suppressedSettings)setting.enabled.value=false;
   if(Settings.CinematicMask.Value){
    vignette.enabled.value=true;vignette.mode.value=VignetteMode.Classic;vignette.color.value=Color.black;
    vignette.center.value=new Vector2(.5f,.5f);vignette.intensity.value=Mathf.Clamp01(CinematicComfort.MaskAlpha)*Settings.MaskStrength.Value;
    vignette.smoothness.value=.8f;vignette.roundness.value=1;vignette.rounded.value=true;
    if(vignette.intensity.value>.03f&&!maskRendered){maskRendered=true;Plugin.Logger.LogInfo("Cinematic vignette submitted to headset post-processing.");}
    if(vignette.intensity.value==0)maskRendered=false;
   }
  }catch(Exception e){Fail(e);}
 }
 static bool Disorienting(string name){
  if(name=="Kino.Datamosh")return true;
  if(!name.StartsWith("Colorful.",StringComparison.Ordinal))return false;
  return name.Contains("Blur",StringComparison.Ordinal)||name.Contains("Chromatic",StringComparison.Ordinal)||name.Contains("Glitch",StringComparison.Ordinal)||name.Contains("Distort",StringComparison.Ordinal)||name.Contains("Wiggle",StringComparison.Ordinal)||name.Contains("Noise",StringComparison.Ordinal)||name.Contains("AnalogTV",StringComparison.Ordinal);
 }
 static void Refresh(Camera camera){
  suppressed.Clear();cachedCamera=camera;lastRefresh=Time.frameCount;
  if(!camera)return;
  foreach(var component in camera.GetComponents<Behaviour>()){
   if(!component)continue;
   if(Disorienting(component.GetIl2CppType().FullName))suppressed.Add(component);
  }
 }
 static void AfterMirror(VRPlayer player,bool refreshEffects){
  if(failed||!Settings.ReduceEffects.Value||!player)return;
  try{
   var camera=player.headCamera;
   if(refreshEffects||camera!=cachedCamera||Time.frameCount-lastRefresh>=30)Refresh(camera);
   foreach(var effect in suppressed)if(effect&&effect.enabled)effect.enabled=false;
  }catch(Exception e){Fail(e);}
 }
 internal static void Tick(float alpha){
  if(failed)return;
  try{
   var p=VRPlayer.Instance;if(!p||!p.headCamera)return;
   var layer=p.headCamera.GetComponent<PostProcessLayer>();
   if(alpha>0&&Settings.CinematicMask.Value){
    if(layer&&layer.m_Resources){
     if(!layer.enabled){layer.enabled=true;temporarilyEnabled=layer;}
    }else if(!missingReported){missingReported=true;Plugin.Logger.LogWarning("Cinematic mask requested but no usable game PostProcessLayer; runtime mask unavailable in this camera state.");}
   }else if(temporarilyEnabled){temporarilyEnabled.enabled=false;temporarilyEnabled=null;}
  }catch(Exception e){Fail(e);}
 }
 static void Fail(Exception e){
  failed=true;if(temporarilyEnabled){temporarilyEnabled.enabled=false;temporarilyEnabled=null;}
  Plugin.Logger.LogError("Comfort post-processing stopped after error: "+e);
 }
 internal static void ResetScene(){
  if(temporarilyEnabled)temporarilyEnabled.enabled=false;
  temporarilyEnabled=null;settingsLayer=null;vignette=null;suppressedSettings.Clear();suppressed.Clear();cachedCamera=null;
  lastRefresh=-100;missingReported=maskRendered=false;
 }
}
