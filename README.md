# NativeCurve

This repo contains a very simple re-implementation of Unity's AnimationCurve, in a Job-friendly and Burst-friendly way.  This allows you to easily evaluate animation curves inside of Jobs on other threads without much effort at all.  It correctly implements spline-based keyframe interpolation, and does not rely on aproximating AnimationCurves by baking out values into dense arrays, and so the results are accurate to at least 4 decimal places.

NativeCurves are read-only, once they are constructed they cannot be modified.  This is to make it as easy as possible to use these curves in multithreaded environments.

Note that this implementation ignores the custom "weighting" that can sometimes be used in AnimationCurves, due to the difficulty in re-implementing that spline parameterization.  If I can find a good way to implement weighting I will update this package to support that as well, but for now it should work well for non-weighted curves!

# Example Usage

```csharp

void RunJob() {
  AnimationCurve curve = ...
  
  new MyCurveJob() {
    //Use the ToNative extension method to convert an AnimationCurve to a NativeCurve
    Curve = curve.ToNative() 
  }.Run(100);

  //No need to clean up Curve here, deallocated automatically by job!
}

public struct MyCurveJob : IJobParallelFor {

  [DeallocateOnJobCompletion] //Curves can be deallocated when jobs are completed
  public NativeCurve Curve;

  public void Execute(int index) {
    //Call Evaluate just like you would with AnimationCurve
    //Curves are ReadOnly by default so no need to tag with [ReadOnly]
    float value = Curve.Evaluate(0.14f);
  }
}
```
