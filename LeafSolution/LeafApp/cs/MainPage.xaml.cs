using System;
using System.IO;
using System.Collections;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.Devices.Enumeration;
using Windows.Foundation;
using Windows.Foundation.Metadata;
using Windows.Graphics.Display;
using Windows.Graphics.Imaging;
using Windows.Media;
using Windows.Media.Capture;
using Windows.Media.FaceAnalysis;
using Windows.Media.MediaProperties;
using Windows.Phone.UI.Input;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.Storage.Streams;
using Windows.System.Display;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;
using Microsoft.ProjectOxford.Face;
using Microsoft.ProjectOxford.Face.Contract;
using Microsoft.ProjectOxford.Common.Contract;
using System.Collections.Generic;
using System.Net.Http;
using System.Net;
using System.Net.NetworkInformation;
using Windows.System.Threading;
using Windows.Media.Core;
using Windows.UI.Xaml.Shapes;
using Windows.UI;
using MySql.Data.MySqlClient;

namespace CameraStarterKit
{


    public sealed partial class MainPage : Page
    {
        // Rotation metadata to apply to the preview stream and recorded videos (MF_MT_VIDEO_ROTATION)
        // Reference: http://msdn.microsoft.com/en-us/library/windows/apps/xaml/hh868174.aspx
        private static readonly Guid RotationKey = new Guid("C380465D-2271-428C-9B83-ECEA3B4A85C1");

        // Folder in which the captures will be stored (initialized in SetupUiAsync)
        private StorageFolder _captureFolder = null;
        // Prevent the screen from sleeping while the camera is running
        private readonly DisplayRequest _displayRequest = new DisplayRequest();

        // For listening to media property changes
        private readonly SystemMediaTransportControls _systemMediaControls = SystemMediaTransportControls.GetForCurrentView();

        // MediaCapture and its state variables
        private MediaCapture _mediaCapture;
        private bool _isInitialized;
        private bool _isPreviewing;
        private bool _isRecording;

        private FaceDetectionEffect _faceDetectionEffect;
        private IMediaEncodingProperties _previewProperties;
        ThreadPoolTimer PeriodicTimer;
        //seconds to next picture if face is detected
        public int period = 3;

        // UI state
        private bool _isSuspending;
        private bool _isActivePage;
        private bool _isUIActive;
        private Task _setupTask = Task.CompletedTask;

        // Information about the camera device
        private bool _mirroringPreview;
        private bool _externalCamera;

        // Rotation Helper to simplify handling rotation compensation for the camera streams
        private CameraRotationHelper _rotationHelper;

        private readonly IFaceServiceClient faceServiceClient = new FaceServiceClient("578d0d80758946c0a9f0432e604970f0", "https://westeurope.api.cognitive.microsoft.com/face/v1.0");
        const string ConnectionString = "Server=192.168.43.77;Port=3306;Database=emotions;Uid=Benny;Pwd=leafshmeaf34sieb;Encrypt=false;";
        #region Constructor, lifecycle and navigation

        public MainPage()
        {
            this.InitializeComponent();
            // Do not cache the state of the UI when suspending/navigating
            NavigationCacheMode = NavigationCacheMode.Disabled;
        }

        private void Application_Suspending(object sender, SuspendingEventArgs e)
        {
            _isSuspending = false;
            var deferral = e.SuspendingOperation.GetDeferral();
            var task = Dispatcher.RunAsync(CoreDispatcherPriority.High, async () =>
            {
                await SetUpBasedOnStateAsync();
                deferral.Complete();
            });
        }

        private void Application_Resuming(object sender, object o)
        {
            _isSuspending = false;

            var task = Dispatcher.RunAsync(CoreDispatcherPriority.High, async () =>
            {
                await SetUpBasedOnStateAsync();
            });
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            // Useful to know when to initialize/clean up the camera
            Application.Current.Suspending += Application_Suspending;
            Application.Current.Resuming += Application_Resuming;
            Window.Current.VisibilityChanged += Window_VisibilityChanged;

            _isActivePage = true;
            await SetUpBasedOnStateAsync();
        }

        protected override async void OnNavigatingFrom(NavigatingCancelEventArgs e)
        {
            // Handling of this event is included for completenes, as it will only fire when navigating between pages and this sample only includes one page
            Application.Current.Suspending -= Application_Suspending;
            Application.Current.Resuming -= Application_Resuming;
            Window.Current.VisibilityChanged -= Window_VisibilityChanged;
            _isActivePage = false;
            await SetUpBasedOnStateAsync();
        }

        #endregion Constructor, lifecycle and navigation

        #region Event handlers
        private async void Window_VisibilityChanged(object sender, VisibilityChangedEventArgs args)
        {
            await SetUpBasedOnStateAsync();
        }

        private async void FaceDetect()
        {
            var faceDetectionDefinition = new FaceDetectionEffectDefinition();
            faceDetectionDefinition.DetectionMode = FaceDetectionMode.Balanced;
            faceDetectionDefinition.SynchronousDetectionEnabled = false;
            _faceDetectionEffect = (FaceDetectionEffect)await _mediaCapture.AddVideoEffectAsync(faceDetectionDefinition, MediaStreamType.VideoPreview);
            _faceDetectionEffect.FaceDetected += FaceDetectionEffect_FaceDetected;
            _faceDetectionEffect.DesiredDetectionInterval = TimeSpan.FromMilliseconds(33);
            _faceDetectionEffect.Enabled = true;
        }

        private async void FaceStopDetect()
        {
            _faceDetectionEffect.Enabled = false;
            _faceDetectionEffect.FaceDetected -= FaceDetectionEffect_FaceDetected;
            await _mediaCapture.ClearEffectsAsync(MediaStreamType.VideoPreview);
            _faceDetectionEffect = null; this.cvsFaceOverlay.Children.Clear();
            this.cvsFaceOverlay.Children.Clear();
        }

        private async void FaceDetectionEffect_FaceDetected(FaceDetectionEffect sender, FaceDetectedEventArgs args)
        {
            var detectedFaces = args.ResultFrame.DetectedFaces;
            await Dispatcher
              .RunAsync(CoreDispatcherPriority.Normal,
                () => DrawFaceBoxes(detectedFaces));
        }

        private void DrawFaceBoxes(IReadOnlyList<DetectedFace> detectedFaces)
        {
            cvsFaceOverlay.Children.Clear();
            for (int i = 0; i < detectedFaces.Count; i++)
            {
                var face = detectedFaces[i];
                var faceBounds = face.FaceBox;
                Rectangle faceHighlightRectangle = new Rectangle()
                {
                    Height = faceBounds.Height,
                    Width = faceBounds.Width
                };
                Canvas.SetLeft(faceHighlightRectangle, faceBounds.X);
                Canvas.SetTop(faceHighlightRectangle, faceBounds.Y);
                faceHighlightRectangle.StrokeThickness = 2;
                faceHighlightRectangle.Stroke = new SolidColorBrush(Colors.LightCyan);
                cvsFaceOverlay.Children.Add(faceHighlightRectangle);
            }
        }

        private async void PhotoButton_Click(object sender, RoutedEventArgs e)
        {
            await AnalyzeFace();
        }

        private async Task AnalyzeFace()
        {

            var requiredFaceAttributes = new FaceAttributeType[] {
                    FaceAttributeType.Age,
                    FaceAttributeType.Gender,
                    FaceAttributeType.Emotion,
                    FaceAttributeType.FacialHair,
                    FaceAttributeType.Glasses
            };

            using (var captureStream = new InMemoryRandomAccessStream())
            {
                Guid uid;
                float mood = 0.5f;
                await _mediaCapture.CapturePhotoToStreamAsync(ImageEncodingProperties.CreateJpeg(), captureStream);
                captureStream.Seek(0);
                var faces = await faceServiceClient.DetectAsync(captureStream.AsStream(), returnFaceLandmarks: true, returnFaceAttributes: requiredFaceAttributes);

                foreach (var face in faces)
                {
                    var emotion = face.FaceAttributes.Emotion;
                    var attributes = face.FaceAttributes;
                    var age = attributes.Age;
                    var gender = attributes.Gender;
                    var facialHair = attributes.FacialHair;
                    var glasses = attributes.Glasses;
                    //get the mood from all emotions
                    mood = emotion.Neutral / 2 + (emotion.Happiness - ((emotion.Sadness + emotion.Anger) / 2));
                    mood = Math.Clamp(mood, 0.0f, 1.0f);
                    emotionList.Items.Add("mood: " + mood);
                    emotionList.ScrollIntoView(emotionList.Items[emotionList.Items.Count - 1]);
                }
                
              //  await SaveToDB(mood, uid);
            };
        }
        public int userID = 1;
        private async Task SaveToDB(float mood, Guid uid)
        {
            using (MySqlConnection sqlConn = new MySqlConnection(ConnectionString))
            {
                sqlConn.Open();
                ConnectionText.Text = ("connection status: " + sqlConn.State.ToString());
                if (sqlConn.State.ToString() == "Open")
                {
                    ConnectionText.Foreground = new SolidColorBrush(Colors.Teal);
                }
                else
                {
                    ConnectionText.Foreground = new SolidColorBrush(Colors.PaleVioletRed);
                }
                MySqlCommand cmd = new MySqlCommand();// Creating instance of SqlCommand
                cmd.Connection = sqlConn; // set the connection to instance of SqlCommand
                cmd.CommandText = "INSERT INTO emotiondata VALUES(" + userID + ", Now()," + mood + ");";
                cmd.ExecuteNonQuery();
                sqlConn.Close();
            }
        }

        private void VideoButton_Click(object sender, RoutedEventArgs e)
        {
            if (!_isRecording)
            {
                StartTimers();
                _isRecording = true;
                FaceDetect();
            }
            else
            {
                Debug.WriteLine("Stopping recording...");
                _isRecording = false;
            }

            // After starting or stopping video recording, update the UI to reflect the MediaCapture state
            UpdateCaptureControls();
        }


        #endregion Event handlers


        #region MediaCapture methods

        /// <summary>
        /// Initializes the MediaCapture, registers events, gets camera device information for mirroring and rotating, starts preview and unlocks the UI
        /// </summary>
        /// <returns></returns>
        private async Task InitializeCameraAsync()
        {
            Debug.WriteLine("InitializeCameraAsync");

            if (_mediaCapture == null)
            {
                // Attempt to get the back camera if one is available, but use any camera device if not
                var cameraDevice = await FindCameraDeviceByPanelAsync(Windows.Devices.Enumeration.Panel.Back);

                if (cameraDevice == null)
                {
                    Debug.WriteLine("No camera device found!");
                    return;
                }

                // Create MediaCapture and its settings
                _mediaCapture = new MediaCapture();



                var settings = new MediaCaptureInitializationSettings { VideoDeviceId = cameraDevice.Id };

                // Initialize MediaCapture
                try
                {
                    await _mediaCapture.InitializeAsync(settings);
                    _isInitialized = true;
                }
                catch (UnauthorizedAccessException)
                {
                    Debug.WriteLine("The app was denied access to the camera");
                }

                // If initialization succeeded, start the preview
                if (_isInitialized)
                {
                    // Figure out where the camera is located
                    if (cameraDevice.EnclosureLocation == null || cameraDevice.EnclosureLocation.Panel == Windows.Devices.Enumeration.Panel.Unknown)
                    {
                        // No information on the location of the camera, assume it's an external camera, not integrated on the device
                        _externalCamera = true;
                    }
                    else
                    {
                        // Camera is fixed on the device
                        _externalCamera = false;

                        // Only mirror the preview if the camera is on the front panel
                        _mirroringPreview = (cameraDevice.EnclosureLocation.Panel == Windows.Devices.Enumeration.Panel.Front);
                    }

                    // Initialize rotationHelper
                    _rotationHelper = new CameraRotationHelper(cameraDevice.EnclosureLocation);
                    _rotationHelper.OrientationChanged += RotationHelper_OrientationChanged;

                    await StartPreviewAsync();

                    UpdateCaptureControls();
                }
            }
        }

        /// <summary>
        /// Handles an orientation changed event
        /// </summary>
        private async void RotationHelper_OrientationChanged(object sender, bool updatePreview)
        {
            if (updatePreview)
            {
                await SetPreviewRotationAsync();
            }
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => UpdateButtonOrientation());
        }

        /// <summary>
        /// Uses the current device orientation in space and page orientation on the screen to calculate the rotation
        /// transformation to apply to the controls
        /// </summary>
        private void UpdateButtonOrientation()
        {
            // Rotate the buttons in the UI to match the rotation of the device
            var angle = CameraRotationHelper.ConvertSimpleOrientationToClockwiseDegrees(_rotationHelper.GetUIOrientation());
            var transform = new RotateTransform { Angle = angle };

            // The RenderTransform is safe to use (i.e. it won't cause layout issues) in this case, because these buttons have a 1:1 aspect ratio
            PhotoButton.RenderTransform = transform;
            VideoButton.RenderTransform = transform;
        }

        /// <summary>
        /// Starts the preview and adjusts it for for rotation and mirroring after making a request to keep the screen on
        /// </summary>
        /// <returns></returns>
        private async Task StartPreviewAsync()
        {
            // Prevent the device from sleeping while the preview is running
            _displayRequest.RequestActive();

            // Set the preview source in the UI and mirror it if necessary
            PreviewControl.Source = _mediaCapture;
            PreviewControl.FlowDirection = _mirroringPreview ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;

            // Start the preview
            await _mediaCapture.StartPreviewAsync();
            _isPreviewing = true;

            // Initialize the preview to the current orientation
            if (_isPreviewing)
            {
                await SetPreviewRotationAsync();
            }
        }

        /// <summary>
        /// Gets the current orientation of the UI in relation to the device (when AutoRotationPreferences cannot be honored) and applies a corrective rotation to the preview
        /// </summary>
        private async Task SetPreviewRotationAsync()
        {
            // Only need to update the orientation if the camera is mounted on the device
            if (_externalCamera) return;

            // Add rotation metadata to the preview stream to make sure the aspect ratio / dimensions match when rendering and getting preview frames
            var rotation = _rotationHelper.GetCameraPreviewOrientation();
            var props = _mediaCapture.VideoDeviceController.GetMediaStreamProperties(MediaStreamType.VideoPreview);
            props.Properties.Add(RotationKey, CameraRotationHelper.ConvertSimpleOrientationToClockwiseDegrees(rotation));
            await _mediaCapture.SetEncodingPropertiesAsync(MediaStreamType.VideoPreview, props, null);
        }


        DispatcherTimer dispatcherTimer;
        DateTimeOffset startTime;
        DateTimeOffset lastTime;
        DateTimeOffset stopTime;
        int timesTicked = 1;
        int timesToTick = 10;
        public void StartTimers()
        {
            dispatcherTimer = new DispatcherTimer();
            dispatcherTimer.Tick += dispatcherTimer_Tick;
            dispatcherTimer.Interval = new TimeSpan(0, 0, 5);
            //IsEnabled defaults to false
            startTime = DateTimeOffset.Now;
            lastTime = startTime;
            dispatcherTimer.Start();
            //IsEnabled should now be true after calling start
        }

        public async void dispatcherTimer_Tick(object sender, object e)
        {
            if (_isRecording)
            {
                await AnalyzeFace();
                Debug.WriteLine("analyzing face...");
            }
        }


        #endregion MediaCapture methods


        #region Helper functions

        /// <summary>
        /// Initialize or clean up the camera and our UI,
        /// depending on the page state.
        /// </summary>
        /// <returns></returns>
        private async Task SetUpBasedOnStateAsync()
        {
            // Avoid reentrancy: Wait until nobody else is in this function.
            while (!_setupTask.IsCompleted)
            {
                await _setupTask;
            }

            // We want our UI to be active if
            // * We are the current active page.
            // * The window is visible.
            // * The app is not suspending.
            bool wantUIActive = _isActivePage && Window.Current.Visible && !_isSuspending;

            if (_isUIActive != wantUIActive)
            {
                _isUIActive = wantUIActive;

                Func<Task> setupAsync = async () =>
                {
                    if (wantUIActive)
                    {
                        await SetupUiAsync();
                        await InitializeCameraAsync();
                    }
                    else
                    {
                        await CleanupUiAsync();
                    }
                };
                _setupTask = setupAsync();
            }

            await _setupTask;
        }

        /// <summary>
        /// Attempts to lock the page orientation, hide the StatusBar (on Phone) and registers event handlers for hardware buttons and orientation sensors
        /// </summary>
        /// <returns></returns>
        private async Task SetupUiAsync()
        {
            // Attempt to lock page to landscape orientation to prevent the CaptureElement from rotating, as this gives a better experience
            DisplayInformation.AutoRotationPreferences = DisplayOrientations.Landscape;

            // Hide the status bar
            if (ApiInformation.IsTypePresent("Windows.UI.ViewManagement.StatusBar"))
            {
                await Windows.UI.ViewManagement.StatusBar.GetForCurrentView().HideAsync();
            }

            var picturesLibrary = await StorageLibrary.GetLibraryAsync(KnownLibraryId.Pictures);
            // Fall back to the local app storage if the Pictures Library is not available
            _captureFolder = picturesLibrary.SaveFolder ?? ApplicationData.Current.LocalFolder;
        }

        /// <summary>
        /// Unregisters event handlers for hardware buttons and orientation sensors, allows the StatusBar (on Phone) to show, and removes the page orientation lock
        /// </summary>
        /// <returns></returns>
        private async Task CleanupUiAsync()
        {


            // Show the status bar
            if (ApiInformation.IsTypePresent("Windows.UI.ViewManagement.StatusBar"))
            {
                await Windows.UI.ViewManagement.StatusBar.GetForCurrentView().ShowAsync();
            }

            // Revert orientation preferences
            DisplayInformation.AutoRotationPreferences = DisplayOrientations.None;
        }

        /// <summary>
        /// This method will update the icons, enable/disable and show/hide the photo/video buttons depending on the current state of the app and the capabilities of the device
        /// </summary>
        private void UpdateCaptureControls()
        {
            // The buttons should only be enabled if the preview started sucessfully
            PhotoButton.IsEnabled = _isPreviewing;
            VideoButton.IsEnabled = _isPreviewing;

            // Update recording button to show "Stop" icon instead of "Record" icon
            StartRecordingIcon.Visibility = _isRecording ? Visibility.Collapsed : Visibility.Visible;
            StopRecordingIcon.Visibility = _isRecording ? Visibility.Visible : Visibility.Collapsed;

        }

        /// <summary>
        /// Registers event handlers for hardware buttons and orientation sensors, and performs an initial update of the UI rotation
        /// </summary>


        /// <summary>
        /// Attempts to find and return a device mounted on the panel specified, and on failure to find one it will return the first device listed
        /// </summary>
        /// <param name="desiredPanel">The desired panel on which the returned device should be mounted, if available</param>
        /// <returns></returns>
        private static async Task<DeviceInformation> FindCameraDeviceByPanelAsync(Windows.Devices.Enumeration.Panel desiredPanel)
        {
            // Get available devices for capturing pictures
            var allVideoDevices = await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture);

            // Get the desired camera by panel
            DeviceInformation desiredDevice = allVideoDevices.FirstOrDefault(x => x.EnclosureLocation != null && x.EnclosureLocation.Panel == desiredPanel);

            // If there is no device mounted on the desired panel, return the first device found
            return desiredDevice ?? allVideoDevices.FirstOrDefault();
        }

        /// <summary>
        /// Applies the given orientation to a photo stream and saves it as a StorageFile
        /// </summary>
        /// <param name="stream">The photo stream</param>
        /// <param name="file">The StorageFile in which the photo stream will be saved</param>
        /// <param name="photoOrientation">The orientation metadata to apply to the photo</param>
        /// <returns></returns>
        private static async Task ReencodeAndSavePhotoAsync(IRandomAccessStream stream, StorageFile file, PhotoOrientation photoOrientation)
        {
            using (var inputStream = stream)
            {
                var decoder = await BitmapDecoder.CreateAsync(inputStream);

                using (var outputStream = await file.OpenAsync(FileAccessMode.ReadWrite))
                {
                    var encoder = await BitmapEncoder.CreateForTranscodingAsync(outputStream, decoder);

                    var properties = new BitmapPropertySet { { "System.Photo.Orientation", new BitmapTypedValue(photoOrientation, PropertyType.UInt16) } };

                    await encoder.BitmapProperties.SetPropertiesAsync(properties);
                    await encoder.FlushAsync();
                }
            }
        }

        private Rectangle MapRectangleToDetectedFace(BitmapBounds detectedfaceBoxCoordinates)
        {
            var faceRectangle = new Rectangle();
            var previewStreamPropterties =
              _previewProperties as VideoEncodingProperties;
            double mediaStreamWidth = previewStreamPropterties.Width;
            double mediaStreamHeight = previewStreamPropterties.Height;
            var faceHighlightRect = LocatePreviewStreamCoordinates(previewStreamPropterties,
              this.PreviewControl);
            faceRectangle.Width = (detectedfaceBoxCoordinates.Width / mediaStreamWidth) *
              faceHighlightRect.Width;
            faceRectangle.Height = (detectedfaceBoxCoordinates.Height / mediaStreamHeight) *
              faceHighlightRect.Height;
            var x = (detectedfaceBoxCoordinates.X / mediaStreamWidth) *
              faceHighlightRect.Width;
            var y = (detectedfaceBoxCoordinates.Y / mediaStreamHeight) *
              faceHighlightRect.Height;
            Canvas.SetLeft(faceRectangle, x);
            Canvas.SetTop(faceRectangle, y);
            return faceRectangle;
        }
        public Rect LocatePreviewStreamCoordinates(
          VideoEncodingProperties previewResolution,
          CaptureElement previewControl)
        {
            var uiRectangle = new Rect();
            var mediaStreamWidth = previewResolution.Width;
            var mediaStreamHeight = previewResolution.Height;
            uiRectangle.Width = previewControl.ActualWidth;
            uiRectangle.Height = previewControl.ActualHeight;
            var uiRatio = previewControl.ActualWidth / previewControl.ActualHeight;
            var mediaStreamRatio = mediaStreamWidth / mediaStreamHeight;
            if (uiRatio > mediaStreamRatio)
            {
                var scaleFactor = previewControl.ActualHeight / mediaStreamHeight;
                var scaledWidth = mediaStreamWidth * scaleFactor;
                uiRectangle.X = (previewControl.ActualWidth - scaledWidth) / 2.0;
                uiRectangle.Width = scaledWidth;
            }
            else
            {
                var scaleFactor = previewControl.ActualWidth / mediaStreamWidth;
                var scaledHeight = mediaStreamHeight * scaleFactor;
                uiRectangle.Y = (previewControl.ActualHeight - scaledHeight) / 2.0;
                uiRectangle.Height = scaledHeight;
            }
            return uiRectangle;
        }



        #endregion Helper functions

    }
}