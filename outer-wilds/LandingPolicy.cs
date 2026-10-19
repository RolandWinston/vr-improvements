using System;
namespace LocalOuterVR {
 public static class LandingPolicy {
  public static bool CanStart(bool seated,bool alive,bool powered,bool manual,bool ground,float clearance,float speed,float slope){
   return seated&&alive&&powered&&!manual&&ground&&Finite(clearance)&&Finite(speed)&&Finite(slope)&&clearance>=0&&clearance<=120&&speed<=12&&slope<=30;
  }
  public static bool CanBrake(float clearance,float downwardSpeed,float maxThrust){
   if(!Finite(clearance)||!Finite(downwardSpeed)||!Finite(maxThrust)||maxThrust<=0)return false;
   float speed=Math.Max(0,downwardSpeed);
   return speed<2 || clearance>1+(speed*speed-0.36f)/(2*Math.Max(1,maxThrust*0.45f));
  }
  public static bool Continue(bool seated,bool alive,bool powered,bool manual,bool sameTarget,bool ground,bool contact){return seated&&alive&&powered&&!manual&&sameTarget&&ground&&!contact;}
  public static bool Finite(float x){return !float.IsNaN(x)&&!float.IsInfinity(x);}
  public static float DescentSpeed(float clearance,float horizontalSpeed,float tilt){
   if(!Finite(clearance)||!Finite(horizontalSpeed)||!Finite(tilt))return 0;
   if(horizontalSpeed>0.8f||tilt>8f)return 0;
   return (float)Math.Min(5,Math.Sqrt(0.36+1.6*Math.Max(0,clearance-2)));
  }
  public static float Demand(float velocityError,float maxThrust,float dt){
   if(!Finite(velocityError)||!Finite(maxThrust)||!Finite(dt)||maxThrust<=0||dt<=0)return 0;
   return Math.Max(-1,Math.Min(1,velocityError/(maxThrust*dt)));
  }
 }
}
