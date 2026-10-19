using BepInEx.Configuration;
namespace Codex.MiSideComfort;
internal static class Settings {
 internal static ConfigEntry<bool> Subtitles, FreezeGlyphMotion, CinematicMask, LockScriptedYaw, ReduceEffects;
 internal static ConfigEntry<float> Distance, BelowEyes, TextSize, MaskStrength, FadeIn, FadeOut;
 internal static void Bind(ConfigFile c){
  Subtitles=c.Bind("Subtitles","Enabled",true,"Stabilize only main-character DPlayer subtitles before rendering.");
  FreezeGlyphMotion=c.Bind("Subtitles","PreventFaceApproach",true,"Keep per-letter geometry at its authored resting position; preserve text, typing, alpha and dialogue events.");
  Distance=c.Bind("Subtitles","DistanceMeters",2f,new ConfigDescription("Distance from the headset.",new AcceptableValueRange<float>(1.4f,3.5f)));
  BelowEyes=c.Bind("Subtitles","BelowEyesMeters",.38f,new ConfigDescription("Positive lowers subtitles.",new AcceptableValueRange<float>(.1f,.8f)));
  TextSize=c.Bind("Subtitles","RelativeTextSize",.75f,new ConfigDescription("Angular size relative to original at 1m.",new AcceptableValueRange<float>(.6f,1.25f)));
  CinematicMask=c.Bind("Cinematics","PeripheralMask",true,"Only scripted/non-user camera movement triggers the mask. Walking, stick turns and physical head turns do not trigger it.");
  LockScriptedYaw=c.Bind("Cinematics","PreventForcedYaw",true,"Preserve tracking yaw during known player-backed cinematic camera takeovers; rebase on release.");
  MaskStrength=c.Bind("Cinematics","MaskStrength",.65f,new ConfigDescription("Maximum peripheral darkening; centre stays visible.",new AcceptableValueRange<float>(.1f,.85f)));
  FadeIn=c.Bind("Cinematics","FadeInSeconds",.12f,new ConfigDescription("Mask fade-in.",new AcceptableValueRange<float>(.03f,.5f)));
  FadeOut=c.Bind("Cinematics","FadeOutSeconds",.35f,new ConfigDescription("Mask fade-out after authored camera motion ends.",new AcceptableValueRange<float>(.1f,1f)));
  ReduceEffects=c.Bind("Effects","ReduceDisorientingEffects",true,"Disable selected blur, chromatic distortion, grain and glitch effects on the VR camera; retain story transitions and ordinary colour grading.");
 }
}
