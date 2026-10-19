using System;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using MiSide_VR.Core;
using UnityEngine;
namespace Codex.MiSideComfort;
[BepInPlugin("com.codex.miside.comfort", "MiSide Comfort", "2.0.0")]
[BepInDependency("com.Glitchtest51.MiSide_VR", BepInDependency.DependencyFlags.HardDependency)]
[BepInProcess("MiSideFull.exe")]
public sealed class Plugin:BasePlugin {
 internal static ManualLogSource Logger;
 static bool renderFailed;
 public override void Load(){
  Logger=Log;Settings.Bind(Config);
  var h=new Harmony("com.codex.miside.comfort");
  try {
  SubtitleLayout.Install(h);CinematicComfort.Install(h);ComfortEffects.Install(h);
  h.Patch(AccessTools.Method(typeof(VRPlayer),"ApplyBeforeRenderPose"),postfix:new HarmonyMethod(typeof(Plugin),nameof(BeforeRender)));
  var adapter=AccessTools.TypeByName("MiSide_VR.UI.Patches.PlayerDialogueAdapter");
  h.Patch(AccessTools.Method(adapter,"Reset"),postfix:new HarmonyMethod(typeof(Plugin),nameof(ResetScene)));
  } catch(Exception e){h.UnpatchSelf();Log.LogError("Comfort installation failed; partial Harmony patches removed: "+e);throw;}
  Camera.onPreCull+=new Action<Camera>(BeforeCameraCull);
  Canvas.willRenderCanvases+=new Action(BeforeCanvasDraw);
  Log.LogInfo("Comfort2.0 hooks registered: early dialogue registration, final-draw glyph guard, cinematic-only mask/yaw, reduced VR effects. Manual acceptance pending.");
 }
 static void BeforeRender(){
  if(renderFailed)return;
  try{CinematicComfort.Tick();SubtitleLayout.Apply();ComfortEffects.Tick(CinematicComfort.MaskAlpha);}
  catch(Exception e){renderFailed=true;Logger.LogError("Comfort frame hook stopped after error: "+e);}
 }
 static void BeforeCameraCull(Camera camera){
  var p=VRPlayer.Instance;
  if(p&&(camera==p.headCamera||camera==p.desktopCamera||camera==p.headsetUiCamera))SubtitleLayout.Apply();
 }
 static void BeforeCanvasDraw(){SubtitleLayout.Apply();}
 static void ResetScene(){SubtitleLayout.Reset();CinematicComfort.Reset();ComfortEffects.ResetScene();renderFailed=false;}
}
