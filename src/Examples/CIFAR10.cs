// Copyright (c) .NET Foundation and Contributors.  All Rights Reserved.  See LICENSE in the project root for license information.
using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Diagnostics;

using static TorchSharp.torch;
using static TorchSharp.torch.nn;
using static TorchSharp.torch.nn.functional;

namespace TorchSharp.Examples
{
    /// <summary>
    /// Driver for various models trained and evaluated on the CIFAR10 small (32x32) color image data set.
    /// </summary>
    /// <remarks>
    /// The dataset for this example can be found at: https://www.cs.toronto.edu/~kriz/cifar.html
    /// Download the binary file, and place it in a dedicated folder, e.g. 'CIFAR10,' then edit
    /// the '_dataLocation' definition below to point at the right folder.
    ///
    /// Note: so far, CIFAR10 is supported, but not CIFAR100.
    /// </remarks>
    class CIFAR10
    {
        private readonly static string _dataset = "CIFAR10";
        private readonly static string _dataLocation = Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "..", "Downloads", _dataset);

        private static int _epochs = 8;
        private static int _trainBatchSize = 64;
        private static int _testBatchSize = 128;

        private readonly static int _logInterval = 25;
        private readonly static int _numClasses = 10;

        private readonly static int _timeout = 3600;    // One hour by default.

        internal static void Main(string[] args)
        {
            torch.random.manual_seed(1);

            var device =
                // This worked on a GeForce RTX 2080 SUPER with 8GB, for all the available network architectures.
                // It may not fit with less memory than that, but it's worth modifying the batch size to fit in memory.
                torch.cuda.is_available() ? torch.CUDA :
                torch.CPU;

            if (device.type == DeviceType.CUDA) {
                _trainBatchSize *= 8;
                _testBatchSize *= 8;
                _epochs *= 16;
            }

            var modelName = args.Length > 0 ? args[0] : "AlexNet";
            var epochs = args.Length > 1 ? int.Parse(args[1]) : _epochs;
            var timeout = args.Length > 2 ? int.Parse(args[2]) : _timeout;

            Console.WriteLine();
            Console.WriteLine($"\tRunning {modelName} with {_dataset} on {device.type.ToString()} for {epochs} epochs, terminating after {TimeSpan.FromSeconds(timeout)}.");
            Console.WriteLine();

            var sourceDir = _dataLocation;
            var targetDir = Path.Combine(_dataLocation, "test_data");

            if (!Directory.Exists(targetDir)) {
                Directory.CreateDirectory(targetDir);
                Utils.Decompress.ExtractTGZ(Path.Combine(sourceDir, "cifar-10-binary.tar.gz"), targetDir);
            }

            Console.WriteLine($"\tCreating the model...");

            Module model = null;

            switch (modelName.ToLower()) {
            case "alexnet":
                model = new AlexNet(modelName, _numClasses, device);
                break;
            case "mobilenet":
                model = new MobileNet(modelName, _numClasses, device);
                break;
            case "vgg11":
            case "vgg13":
            case "vgg16":
            case "vgg19":
                model = new VGG(modelName, _numClasses, device);
                break;
            case "resnet18":
                model = ResNet.ResNet18(_numClasses, device);
                break;
            case "resnet34":
                _testBatchSize /= 4;
                model = ResNet.ResNet34(_numClasses, device);
                break;
            case "resnet50":
                _trainBatchSize /= 6;
                _testBatchSize /= 8;
                model = ResNet.ResNet50(_numClasses, device);
                break;
#if false
            // The following is disabled, because they require big CUDA processors in order to run.
            case "resnet101":
                _trainBatchSize /= 6;
                _testBatchSize /= 8;
                model = ResNet.ResNet101(_numClasses, device);
                break;
            case "resnet152":
                _testBatchSize /= 4;
                model = ResNet.ResNet152(_numClasses, device);
                break;
#endif
            }

            var hflip = torchvision.transforms.HorizontalFlip();
            var gray = torchvision.transforms.Grayscale(3);
            var rotate = torchvision.transforms.Rotate(90);
            var contrast = torchvision.transforms.AdjustContrast(1.25);

            Console.WriteLine($"\tPreparing training and test data...");
            Console.WriteLine();

            using (var train = new CIFARReader(targetDir, false, _trainBatchSize, shuffle: true, device: device, transforms: new torchvision.ITransform[] { }))
            using (var test = new CIFARReader(targetDir, true, _testBatchSize, device: device))
            using (var optimizer = torch.optim.Adam(model.parameters(), 0.001)) {

                Stopwatch totalSW = new Stopwatch();
                totalSW.Start();

                for (var epoch = 1; epoch <= epochs; epoch++) {

                    Stopwatch epchSW = new Stopwatch();
                    epchSW.Start();

                    Train(model, optimizer, nll_loss(), train.Data(), epoch, _trainBatchSize, train.Size);
                    Test(model, nll_loss(), test.Data(), test.Size);

                    epchSW.Stop();
                    Console.WriteLine($"Elapsed time for this epoch: {epchSW.Elapsed.TotalSeconds} s.");

                    if (totalSW.Elapsed.TotalSeconds > timeout) break;
                }

                totalSW.Stop();
                Console.WriteLine($"Elapsed training time: {totalSW.Elapsed} s.");
            }

            model.Dispose();
        }

        private static void Train(
            Module model,
            torch.optim.Optimizer optimizer,
            Loss loss,
            IEnumerable<(Tensor, Tensor)> dataLoader,
            int epoch,
            long batchSize,
            long size)
        {
            model.Train();

            int batchId = 1;
            long total = 0;
            long correct = 0;

            Console.WriteLine($"Epoch: {epoch}...");

            using (var d = torch.NewDisposeScope()) {

                foreach (var (data, target) in dataLoader) {

                    optimizer.zero_grad();

                    var prediction = model.forward(data);
                    var lsm = log_softmax(prediction, 1);
                    var output = loss(lsm, target);

                    output.backward();

                    optimizer.step();

                    total += target.shape[0];

                    var predicted = prediction.argmax(1);
                    correct += predicted.eq(target).sum().ToInt64();

                    if (batchId % _logInterval == 0) {
                        var count = Math.Min(batchId * batchSize, size);
                        Console.WriteLine($"\rTrain: epoch {epoch} [{count} / {size}] Loss: {output.ToSingle().ToString("0.000000")} | Accuracy: { ((float)correct / total).ToString("0.000000") }");
                    }

                    batchId++;

                    d.DisposeEverything();
                }
            }
        }

        private static void Test(
            Module model,
            Loss loss,
            IEnumerable<(Tensor, Tensor)> dataLoader,
            long size)
        {
            model.Eval();

            double testLoss = 0;
            long correct = 0;
            int batchCount = 0;

            using (var d = torch.NewDisposeScope()) {

                foreach (var (data, target) in dataLoader) {

                    var prediction = model.forward(data);
                    var lsm = log_softmax(prediction, 1);
                    var output = loss(lsm, target);

                    testLoss += output.ToSingle();
                    batchCount += 1;

                    var predicted = prediction.argmax(1);
                    correct += predicted.eq(target).sum().ToInt64();

                    d.DisposeEverything();
                }
            }

            Console.WriteLine($"\rTest set: Average loss {(testLoss / batchCount).ToString("0.0000")} | Accuracy {((float)correct / size).ToString("0.0000")}");
        }
    }
}
