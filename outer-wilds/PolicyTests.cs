using System;
using LocalOuterVR;
class PolicyTests {
 static int count;static void Check(bool v,string name){count++;if(!v)throw new Exception(name);}
 static void Flight(float startHeight,float horizontal,float fall,float tilt,float gravity){
  float h=startHeight,x=horizontal,v=fall,dt=0.02f,t=0,lastV=0;const float thrust=30;
  while(h>0 && t<90){
   float desired=LandingPolicy.DescentSpeed(h,Math.Abs(x),tilt);
   float ex=-x,ev=desired-v;float mag=(float)Math.Sqrt(ex*ex+ev*ev);
   float demand=LandingPolicy.Demand(mag,thrust,dt);float ax=mag>0?ex/mag*demand*thrust:0;float av=mag>0?ev/mag*demand*thrust:0;
   x+=ax*dt;v+=(av+gravity)*dt;h-=v*dt;tilt=Math.Max(0,tilt-20*dt);t+=dt;lastV=v;
   Check(LandingPolicy.Finite(h)&&Math.Abs(demand)<=1,"finite bounded thrust");
  }
  Check(h<=0&&t<90,"touchdown within timeout");Check(lastV<1.25f,"gentle touchdown");Check(Math.Abs(x)<0.1f,"lateral stabilized");
  Console.WriteLine("TRACE start="+startHeight+" gravity="+gravity+" time="+t.ToString("F2")+" touchdown="+lastV.ToString("F3"));
 }
 static int Main(){
  Check(LandingPolicy.CanStart(true,true,true,false,true,100,10,20),"valid start");
  Check(!LandingPolicy.CanStart(false,true,true,false,true,30,0,0),"unseated");
  Check(!LandingPolicy.CanStart(true,false,true,false,true,30,0,0),"dead");
  Check(!LandingPolicy.CanStart(true,true,false,false,true,30,0,0),"fuel");
  Check(!LandingPolicy.CanStart(true,true,true,true,true,30,0,0),"manual start");
  Check(!LandingPolicy.CanStart(true,true,true,false,false,30,0,0),"no ground");
  Check(!LandingPolicy.CanStart(true,true,true,false,true,121,0,0),"out of range");
  Check(!LandingPolicy.CanStart(true,true,true,false,true,30,13,0),"entry speed");
  Check(!LandingPolicy.CanStart(true,true,true,false,true,30,0,31),"steep slope");
  Check(!LandingPolicy.CanStart(true,true,true,false,true,float.NaN,0,0),"invalid telemetry");
  Check(!LandingPolicy.CanBrake(1,12,30),"unsafe near-ground entry rejected");
  Check(LandingPolicy.CanBrake(20,12,30),"sufficient braking distance");
  Check(LandingPolicy.CanBrake(1,-5,30),"rising is not descending");
  Check(!LandingPolicy.Continue(true,true,true,true,true,true,false),"manual takeover");
  Check(!LandingPolicy.Continue(true,true,true,false,false,true,false),"target changed");
  Check(!LandingPolicy.Continue(true,true,true,false,true,false,false),"ground lost");
  Check(!LandingPolicy.Continue(true,true,true,false,true,true,true),"contact releases");
  Check(!LandingPolicy.Continue(false,true,true,false,true,true,false),"unbuckle releases");
  Check(!LandingPolicy.Continue(true,false,true,false,true,true,false),"death releases");
  Check(!LandingPolicy.Continue(true,true,false,false,true,true,false),"fuel loss releases");
  Check(LandingPolicy.DescentSpeed(100,8,0)==0,"brake lateral before descent");
  Check(LandingPolicy.DescentSpeed(100,0,20)==0,"align before descent");
  Check(LandingPolicy.Demand(9,0,0.02f)==0,"no divide by zero");
  Flight(100,8,0,35,5);Flight(30,0,10,0,8);Flight(3,0,0,0,5);Flight(10,6,0,25,10);
  Console.WriteLine("PASS "+count+" assertions; pure control policy, not real game/VR flight.");return 0;
 }
}
