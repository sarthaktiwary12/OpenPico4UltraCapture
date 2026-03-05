using System;

[Serializable]
public class CameraMetadata
{
    public string cameraId = string.Empty;
    public int cameraSource;
    public int cameraPositionId;
    public string lensFacing = string.Empty;
    public string hardwareLevel = string.Empty;
    public CameraPose pose = default;
    public CameraIntrinsics intrinsics = default;
    public float[] distortion = new float[0];
    public CameraSensor sensor = default;

    public override string ToString()
    {
        return $"CameraMetadata: id={cameraId}, facing={lensFacing}, level={hardwareLevel}, " +
            $"sensor={sensor?.pixelArraySize?.width}x{sensor?.pixelArraySize?.height}";
    }
}

[Serializable]
public class CameraPose
{
    public float[] translation = new float[0];
    public float[] rotation = new float[0];
    public string reference = string.Empty;
}

[Serializable]
public class CameraIntrinsics
{
    public float fx;
    public float fy;
    public float cx;
    public float cy;
    public float skew;
}

[Serializable]
public class CameraIntSize
{
    public int width;
    public int height;
}

[Serializable]
public class CameraFloatSize
{
    public float width;
    public float height;
}

[Serializable]
public class CameraIntRect
{
    public int left;
    public int top;
    public int right;
    public int bottom;
}

[Serializable]
public class CameraSensor
{
    public float[] availableFocalLengths = new float[0];
    public CameraFloatSize physicalSize = default;
    public CameraIntSize pixelArraySize = default;
    public CameraIntRect preCorrectionActiveArraySize = default;
    public CameraIntRect activeArraySize = default;
    public string timestampSource = string.Empty;
}
