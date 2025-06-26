// PlateMLPClassifier.cs
using Accord.Neuro;
using Accord.Neuro.Learning;
using Accord.Neuro.ActivationFunctions; // Убедитесь, что это пространство имен добавлено
using OpenCvSharp;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq; // Добавлено для ToArray() если используется, и для других LINQ операций

namespace L1_oneN
{
    public class PlateMLPClassifier
    {
        private ActivationNetwork _network;
        private const int PatchSize = 24;
        private const int InputSize = PatchSize * PatchSize;
        private const int HiddenLayer1Size = 128;
        private const int HiddenLayer2Size = 64;
        private const int OutputSize = 1;

        private bool _isTrained = false;
        public bool IsTrained => _isTrained;

        public PlateMLPClassifier()
        {
            _network = new ActivationNetwork(
                new SigmoidFunction(2), // Альфа-параметр для SigmoidFunction
                InputSize,
                HiddenLayer1Size,
                HiddenLayer2Size,
                OutputSize
            );
            new NguyenWidrow(_network).Randomize();
        }

        public double Predict(Mat patch)
        {
            if (patch.Rows != PatchSize || patch.Cols != PatchSize || patch.Channels() != 1)
            {
                throw new ArgumentException($"Патч для MLP должен быть размером {PatchSize}x{PatchSize} и одноканальным (grayscale).");
            }
            if (!_isTrained) return 0.0; // Или выбросить исключение, если предсказание без обучения недопустимо

            double[] inputVector = new double[InputSize];
            int k = 0;
            for (int i = 0; i < patch.Rows; i++)
            {
                for (int j = 0; j < patch.Cols; j++)
                {
                    inputVector[k++] = patch.Get<byte>(i, j) / 255.0;
                }
            }
            double[] output = _network.Compute(inputVector);
            return output[0];
        }

        public void Train(List<double[]> inputs, List<double[]> expectedOutputs, int epochs = 1000, double learningRate = 0.1, Action<string> logCallback = null)
        {
            if (inputs == null || expectedOutputs == null || inputs.Count == 0 || inputs.Count != expectedOutputs.Count)
            {
                logCallback?.Invoke("Ошибка: Данные для обучения некорректны или отсутствуют.");
                return;
            }

            // Убедимся, что данные в правильном формате для BackPropagationLearning
            double[][] inputArray = inputs.ToArray();
            double[][] outputArray = expectedOutputs.ToArray();

            var teacher = new BackPropagationLearning(_network)
            {
                LearningRate = learningRate,
                Momentum = 0.5 // Стандартное значение, можно настроить
            };

            logCallback?.Invoke($"Начало обучения MLP: {epochs} эпох, скорость обучения {learningRate:F4}, архитектура: {InputSize}-{HiddenLayer1Size}-{HiddenLayer2Size}-{OutputSize} (все слои Sigmoid)");

            for (int i = 0; i < epochs; i++)
            {
                // RunEpoch ожидает массивы массивов
                double error = teacher.RunEpoch(inputArray, outputArray) / inputs.Count; // Делим на количество выборок для получения средней ошибки
                if (((i + 1) % 50 == 0) || (i == 0)) // Логируем каждые 50 эпох и первую эпоху
                {
                    logCallback?.Invoke($"Эпоха {i + 1}/{epochs}, Средняя ошибка: {error:F6}");
                }
                if (error < 0.005) // Условие ранней остановки
                {
                    logCallback?.Invoke($"Обучение остановлено на эпохе {i + 1} из-за достижения низкой ошибки ({error:F6}).");
                    break;
                }
            }
            _isTrained = true;
            logCallback?.Invoke("Обучение MLP завершено.");
        }

        public bool SaveWeights(string filePath)
        {
            try
            {
                _network.Save(filePath); // Метод Save доступен для ActivationNetwork
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[PlateMLPClassifier] Ошибка сохранения весов: {ex.Message}");
                return false;
            }
        }

        public bool LoadWeights(string filePath)
        {
            try
            {
                if (File.Exists(filePath))
                {
                    // Network.Load является статическим методом
                    var baseLoadedNetwork = Accord.Neuro.Network.Load(filePath);

                    if (baseLoadedNetwork is ActivationNetwork loadedActivationNetwork)
                    {
                        // Проверяем общую структуру сети и количество выходов каждого слоя
                        // Приводим слои к ActivationLayer для доступа к OutputsCount
                        bool architectureMatches = loadedActivationNetwork.InputsCount == InputSize &&
                                                   loadedActivationNetwork.Layers.Length == 3;

                        if (architectureMatches)
                        {
                            // Проверка количества выходов для каждого слоя
                            var layer1 = loadedActivationNetwork.Layers[0] as ActivationLayer;
                            var layer2 = loadedActivationNetwork.Layers[1] as ActivationLayer;
                            var layer3 = loadedActivationNetwork.Layers[2] as ActivationLayer; // Это выходной слой

                            if (layer1 != null && layer1.Neurons.Length == HiddenLayer1Size && // Neurons.Length эквивалентно OutputsCount для ActivationLayer
                                layer2 != null && layer2.Neurons.Length == HiddenLayer2Size &&
                                layer3 != null && layer3.Neurons.Length == OutputSize)
                            {
                                _network = loadedActivationNetwork;
                                _isTrained = true;
                                Console.WriteLine("[PlateMLPClassifier] Веса успешно загружены и архитектура совместима.");
                                return true;
                            }
                            else
                            {
                                Console.WriteLine("[PlateMLPClassifier] Ошибка загрузки весов: архитектура слоев несовместима.");
                                if (layer1 == null || layer1.Neurons.Length != HiddenLayer1Size) Console.WriteLine($" - Layer 0 Outputs: ожидалось {HiddenLayer1Size}, найдено {(layer1?.Neurons.Length ?? -1)} (или слой не ActivationLayer)");
                                if (layer2 == null || layer2.Neurons.Length != HiddenLayer2Size) Console.WriteLine($" - Layer 1 Outputs: ожидалось {HiddenLayer2Size}, найдено {(layer2?.Neurons.Length ?? -1)} (или слой не ActivationLayer)");
                                if (layer3 == null || layer3.Neurons.Length != OutputSize) Console.WriteLine($" - Layer 2 Outputs: ожидалось {OutputSize}, найдено {(layer3?.Neurons.Length ?? -1)} (или слой не ActivationLayer)");
                                _isTrained = false;
                                return false;
                            }
                        }
                        else
                        {
                            Console.WriteLine("[PlateMLPClassifier] Ошибка загрузки весов: базовая архитектура сети (InputsCount или Layers.Length) несовместима.");
                            if (loadedActivationNetwork.InputsCount != InputSize) Console.WriteLine($" - InputsCount: ожидалось {InputSize}, найдено {loadedActivationNetwork.InputsCount}");
                            if (loadedActivationNetwork.Layers.Length != 3) Console.WriteLine($" - Layers.Length: ожидалось 3, найдено {loadedActivationNetwork.Layers.Length}");
                            _isTrained = false;
                            return false;
                        }
                    }
                    else
                    {
                        Console.WriteLine($"[PlateMLPClassifier] Ошибка загрузки весов: загруженный файл не является ActivationNetwork. Тип загруженной сети: {baseLoadedNetwork?.GetType().FullName}");
                        _isTrained = false;
                        return false;
                    }
                }
                else
                {
                    Console.WriteLine($"[PlateMLPClassifier] Файл весов не найден: {filePath}");
                    _isTrained = false;
                    return false;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[PlateMLPClassifier] Исключение при загрузке весов: {ex.Message}");
                _isTrained = false;
                return false;
            }
        }
    }
}