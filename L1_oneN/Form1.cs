// Form1.cs
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
// System.Drawing убрали, чтобы избежать конфликтов, если он не нужен явно для другого
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using OpenCvSharp;
using OpenCvSharp.Extensions; // Для BitmapConverter
// using Accord.Neuro; // Accord.Neuro используется в PlateMLPClassifier
// using OpenCvSharp.ML; // Если не используется напрямую здесь, можно убрать

namespace L1_oneN
{
    public partial class Form1 : Form
    {
        private Mat _originalImage;
        private Mat _processedImage;
        private Mat _scaledBaseImage; // Для перерисовки с учетом состояния HideNoCharCheckBox

        // Структура для хранения информации об элементах для отрисовки
        private struct DrawingInfo
        {
            public Rect Rectangle;
            public Scalar RectColor;
            public string Text;
            public Point TextPosition;
            public HersheyFonts FontFace;
            public double FontScale;
            public Scalar TextColor;
            public int Thickness;
            public bool IsNoCharResult; // Флаг для рамок с "NO_CHAR_RECTS"
        }
        private List<DrawingInfo> _plateDrawingInfos = new List<DrawingInfo>();


        // Обновленные константы для MLP
        private const int MLP_INPUT_SIZE = 24;
        private const double PLATE_ASPECT_RATIO = 520.0 / 115.0;
        private static readonly OpenCvSharp.Size TEMPLATE_CHAR_SIZE = new OpenCvSharp.Size(20, 40);

        private Dictionary<char, List<Mat>> _charTemplatesMulti = new Dictionary<char, List<Mat>>();
        private string _templateChars = "0123456789ABEKMHOPCTYX";

        private PlateMLPClassifier _plateMlp;
        private const string MlpWeightsFile = "plate_mlp_weights.bin";

        private const int MLP_TRAIN_TARGET_PATCH_SIZE = 24;


        public Form1()
        {
            InitializeComponent();

            _plateMlp = new PlateMLPClassifier();
            UpdateMlpStatusLabel();

            LoadCharTemplates("templates");

            // Предполагается, что CheckBox с именем HideNoChar уже добавлен на форму через дизайнер.
            // Если его имя другое, замените "HideNoChar" ниже.
            // Пример, если бы его нужно было создать программно (но он должен быть из дизайнера):
            // this.HideNoChar = new CheckBox { Name = "HideNoChar", Text = "Скрыть 'нет символов'", Location = new System.Drawing.Point( /*...*/ ) };
            // this.Controls.Add(this.HideNoChar);

            // Убедитесь, что у вашего CheckBox в дизайнере установлено имя "HideNoChar"
            // или измените имя в следующей строке на правильное.
            if (this.Controls.ContainsKey("HideNoChar"))
            {
                (this.Controls["HideNoChar"] as CheckBox).CheckedChanged += HideNoChar_CheckedChanged;
            }
            else
            {
                // Можно вывести предупреждение или создать чекбокс программно, если он критичен
                AppendLogUiThread("ПРЕДУПРЕЖДЕНИЕ: CheckBox 'HideNoChar' не найден на форме.");
            }
        }

        private void UpdateMlpStatusLabel()
        {
            if (_plateMlp.LoadWeights(MlpWeightsFile))
            {
                AppendLogUiThread("MLP: Веса успешно загружены.");
                if (this.statusStrip1 != null && this.statusStrip1.Items["toolStripStatusLabel"] != null)
                    ((ToolStripStatusLabel)this.statusStrip1.Items["toolStripStatusLabel"]).Text = "MLP: Готов (веса загружены)";
            }
            else
            {
                AppendLogUiThread("ПРЕДУПРЕЖДЕНИЕ: Веса MLP не загружены. Детектор номеров будет работать некорректно.");
                if (this.statusStrip1 != null && this.statusStrip1.Items["toolStripStatusLabel"] != null)
                    ((ToolStripStatusLabel)this.statusStrip1.Items["toolStripStatusLabel"]).Text = "MLP: Не обучен (веса не найдены)";
            }
        }

        private void btnLoadImage_Click(object sender, EventArgs e)
        {
            using (OpenFileDialog openFileDialog = new OpenFileDialog())
            {
                openFileDialog.Filter = "Image Files(*.BMP;*.JPG;*.JPEG;*.PNG)|*.BMP;*.JPG;*.JPEG;*.PNG|All files (*.*)|*.*";
                if (openFileDialog.ShowDialog() == DialogResult.OK)
                {
                    try
                    {
                        _originalImage?.Dispose();
                        _scaledBaseImage?.Dispose(); // Очищаем также и базовое изображение для перерисовки
                        _processedImage?.Dispose();   // И обработанное
                        _plateDrawingInfos.Clear(); // Очищаем инструкции рисования

                        _originalImage = Cv2.ImRead(openFileDialog.FileName, ImreadModes.Color);
                        if (_originalImage.Empty())
                        {
                            MessageBox.Show("Не удалось загрузить изображение.", "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error);
                            return;
                        }
                        pictureBoxOriginal.Image = OpenCvSharp.Extensions.BitmapConverter.ToBitmap(_originalImage);
                        AppendLogUiThread("--- Новое изображение загружено ---");
                        pictureBoxProcessed.Image = null; // Очищаем предыдущий результат

                        if (_plateMlp.IsTrained)
                        {
                            AppendLogUiThread("Состояние MLP: Веса загружены.");
                        }
                        else
                        {
                            AppendLogUiThread("Состояние MLP: Веса НЕ загружены или MLP не обучен.");
                        }
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"Ошибка при загрузке изображения: {ex.Message}", "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
            }
        }

        private void LoadCharTemplates(string baseTemplatesFolderPath)
        {
            _charTemplatesMulti.Clear();
            if (!Directory.Exists(baseTemplatesFolderPath))
            {
                AppendLogUiThread($"Базовая папка с шаблонами '{baseTemplatesFolderPath}' не найдена.");
                return;
            }

            int totalTemplatesLoaded = 0;
            foreach (char c in _templateChars)
            {
                string charFolderPath = Path.Combine(baseTemplatesFolderPath, c.ToString().ToUpperInvariant());

                if (Directory.Exists(charFolderPath))
                {
                    List<Mat> templatesForChar = new List<Mat>();
                    string[] templateFiles = Directory.GetFiles(charFolderPath, "*.png");

                    if (templateFiles.Length == 0)
                    {
                        // AppendLogUiThread($"Для символа '{c}' в папке '{charFolderPath}' не найдено файлов шаблонов .png.");
                        continue;
                    }

                    foreach (string templatePath in templateFiles)
                    {
                        using (Mat tpl = Cv2.ImRead(templatePath, ImreadModes.Grayscale))
                        {
                            if (!tpl.Empty())
                            {
                                Mat processedTpl = new Mat();
                                Cv2.Threshold(tpl, processedTpl, 128, 255, ThresholdTypes.Binary | ThresholdTypes.Otsu);
                                Cv2.Resize(processedTpl, processedTpl, TEMPLATE_CHAR_SIZE, interpolation: InterpolationFlags.Linear);
                                templatesForChar.Add(processedTpl);
                                totalTemplatesLoaded++;
                            }
                            else
                            {
                                AppendLogUiThread($"Не удалось загрузить шаблон из файла: {templatePath}");
                            }
                        }
                    }

                    if (templatesForChar.Count > 0)
                    {
                        _charTemplatesMulti[c] = templatesForChar;
                        // AppendLogUiThread($"Для символа '{c}' загружено {templatesForChar.Count} шаблонов.");
                    }
                }
                else
                {
                    AppendLogUiThread($"Папка для символа '{c}' не найдена: {charFolderPath}");
                }
            }
            AppendLogUiThread($"Всего загружено {totalTemplatesLoaded} шаблонов для {_charTemplatesMulti.Count} уникальных символов.");
        }


        private async void btnProcess_Click(object sender, EventArgs e)
        {
            if (_originalImage == null || _originalImage.Empty())
            {
                MessageBox.Show("Сначала загрузите изображение.", "Информация", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (_charTemplatesMulti.Count == 0)
            {
                MessageBox.Show("Шаблоны символов не загружены. Распознавание символов невозможно. Проверьте папку 'templates' и лог загрузки шаблонов.", "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            btnProcess.Enabled = false;
            btnLoadImage.Enabled = false;
            if (this.Controls.ContainsKey("btnTrainMLP")) ((Button)this.Controls["btnTrainMLP"]).Enabled = false;
            if (this.Controls.ContainsKey("HideNoChar")) (this.Controls["HideNoChar"] as CheckBox).Enabled = false;


            AppendLogUiThread("Начало обработки...");
            if (this.statusStrip1 != null && this.statusStrip1.Items["toolStripStatusLabel"] != null)
                ((ToolStripStatusLabel)this.statusStrip1.Items["toolStripStatusLabel"]).Text = "Обработка...";
            progressBar.Style = ProgressBarStyle.Marquee;
            progressBar.Value = 0;

            _plateDrawingInfos.Clear(); // Очищаем предыдущие инструкции рисования

            // Создаем _scaledBaseImage - это будет наша основа для рисования
            _scaledBaseImage?.Dispose();
            _scaledBaseImage = _originalImage.Clone();
            if (_scaledBaseImage.Empty())
            {
                AppendLogUiThread("Критическая ошибка: не удалось клонировать _originalImage для _scaledBaseImage.");
                // Восстановление UI
                btnProcess.Enabled = true; btnLoadImage.Enabled = true;
                if (this.Controls.ContainsKey("btnTrainMLP")) ((Button)this.Controls["btnTrainMLP"]).Enabled = true;
                if (this.Controls.ContainsKey("HideNoChar")) (this.Controls["HideNoChar"] as CheckBox).Enabled = true;
                UpdateMlpStatusLabel();
                return;
            }
            Cv2.Resize(_scaledBaseImage, _scaledBaseImage, new OpenCvSharp.Size(640, 480));

            // Изображение в оттенках серого для детекции
            Mat grayImage = new Mat();
            Cv2.CvtColor(_scaledBaseImage, grayImage, ColorConversionCodes.BGR2GRAY); // Используем _scaledBaseImage для получения grayImage

            List<Tuple<Rect, double>> potentialPlates = new List<Tuple<Rect, double>>();
            int[] windowScales = {
                (int)(grayImage.Width * 0.18), (int)(grayImage.Width * 0.20),
                (int)(grayImage.Width * 0.25), (int)(grayImage.Width * 0.35),
                (int)(grayImage.Width * 0.45),
            };
            int stepSizeFactor = 8;

            await Task.Run(() =>
            {
                foreach (int baseWidth in windowScales)
                {
                    int windowW = baseWidth;
                    int windowH = (int)(windowW / PLATE_ASPECT_RATIO);
                    if (windowW < MLP_INPUT_SIZE || windowH < MLP_INPUT_SIZE || windowW <= 0 || windowH <= 0) continue;
                    int stepX = Math.Max(1, windowW / stepSizeFactor);
                    int stepY = Math.Max(1, windowH / stepSizeFactor);

                    for (int y = 0; y <= grayImage.Rows - windowH; y += stepY)
                    {
                        for (int x = 0; x <= grayImage.Cols - windowW; x += stepX)
                        {
                            Rect roiRect = new Rect(x, y, windowW, windowH);
                            using (Mat patch = new Mat(grayImage, roiRect))
                            using (Mat mlpInputPatch = new Mat())
                            {
                                if (patch.Empty()) continue;
                                Cv2.Resize(patch, mlpInputPatch, new OpenCvSharp.Size(MLP_INPUT_SIZE, MLP_INPUT_SIZE), 0, 0, InterpolationFlags.Area);
                                double score = _plateMlp.Predict(mlpInputPatch);
                                if (score > 0.70)
                                {
                                    lock (potentialPlates)
                                    {
                                        potentialPlates.Add(new Tuple<Rect, double>(roiRect, score));
                                    }
                                }
                            }
                        }
                    }
                }
            });

            grayImage.Dispose();

            List<Rect> detectedPlates = ApplySimplifiedNMS(potentialPlates, 0.2);

            int plateCounter = 0;
            foreach (Rect plateRectOriginal in detectedPlates)
            {
                plateCounter++;
                Rect plateRectForOCR = plateRectOriginal;

                if (plateRectForOCR.X < 0 || plateRectForOCR.Y < 0 ||
                    plateRectForOCR.Width <= 0 || plateRectForOCR.Height <= 0 ||
                    plateRectForOCR.Right > _scaledBaseImage.Cols ||
                    plateRectForOCR.Bottom > _scaledBaseImage.Rows)
                {
                    AppendLogUiThread($"ПРЕДУПРЕЖДЕНИЕ: Обнаружен некорректный Rect для номера: {plateRectForOCR}. Пропуск.");
                    continue;
                }

                // Кандидат для OCR берем из _scaledBaseImage (чистое, цветное, масштабированное)
                using (Mat plateCandidate = new Mat(_scaledBaseImage, plateRectForOCR))
                {
                    if (plateCandidate.Empty())
                    {
                        AppendLogUiThread($"ПРЕДУПРЕЖДЕНИЕ: Не удалось создать plateCandidate для Rect: {plateRectForOCR}. Пропуск.");
                        continue;
                    }
                    string recognizedText = RecognizeCharsOnPlate(plateCandidate.Clone());

                    if (recognizedText == null)
                    {
                        AppendLogUiThread($"ПРЕДУПРЕЖДЕНИЕ: RecognizeCharsOnPlate вернула null для области {plateRectForOCR}. Используется \"[OCR_ERR]\".");
                        recognizedText = "[OCR_ERR]";
                    }

                    bool isNoCharRect = recognizedText == "[NO_CHAR_RECTS]";

                    _plateDrawingInfos.Add(new DrawingInfo
                    {
                        Rectangle = plateRectOriginal,
                        RectColor = Scalar.LimeGreen,
                        Text = recognizedText,
                        TextPosition = new OpenCvSharp.Point(plateRectOriginal.X, plateRectOriginal.Y - 10),
                        FontFace = HersheyFonts.HersheySimplex,
                        FontScale = 0.7,
                        TextColor = Scalar.Red,
                        Thickness = 2,
                        IsNoCharResult = isNoCharRect
                    });

                    AppendLogUiThread($"Номер {plateCounter}: {recognizedText} (область: {plateRectOriginal})");
                }
            }

            if (detectedPlates.Count == 0)
            {
                AppendLogUiThread("Номерные знаки не найдены.");
            }
            if (!_plateMlp.IsTrained && detectedPlates.Count > 0)
            {
                AppendLogUiThread("ПРЕДУПРЕЖДЕНИЕ: Найдены кандидаты, но MLP не обучен. Результаты могут быть случайными!");
            }

            RefreshProcessedImageDisplay(); // Первоначальная отрисовка

            AppendLogUiThread("Обработка завершена.");
            progressBar.Style = ProgressBarStyle.Blocks;
            progressBar.Value = progressBar.Maximum;

            btnProcess.Enabled = true;
            btnLoadImage.Enabled = true;
            if (this.Controls.ContainsKey("btnTrainMLP")) ((Button)this.Controls["btnTrainMLP"]).Enabled = true;
            if (this.Controls.ContainsKey("HideNoChar")) (this.Controls["HideNoChar"] as CheckBox).Enabled = true;
            UpdateMlpStatusLabel();
        }

        private void RefreshProcessedImageDisplay()
        {
            if (_scaledBaseImage == null || _scaledBaseImage.Empty())
            {
                _processedImage?.Dispose();
                _processedImage = null;
                pictureBoxProcessed.Image = null;
                return;
            }

            _processedImage?.Dispose();
            _processedImage = _scaledBaseImage.Clone();

            bool hideNoCharResults = false;
            // Убедитесь, что имя "HideNoChar" соответствует имени вашего чекбокса в дизайнере
            if (this.Controls.ContainsKey("HideNoChar") && this.Controls["HideNoChar"] is CheckBox hideNoCharCb)
            {
                hideNoCharResults = hideNoCharCb.Checked;
            }

            foreach (var drawingInfo in _plateDrawingInfos)
            {
                if (drawingInfo.IsNoCharResult && hideNoCharResults)
                {
                    continue; // Пропускаем рисование, если это "пустой" результат и чекбокс отмечен
                }

                Cv2.Rectangle(_processedImage, drawingInfo.Rectangle, drawingInfo.RectColor, drawingInfo.Thickness);

                if (!string.IsNullOrEmpty(drawingInfo.Text))
                {
                    Cv2.PutText(_processedImage, drawingInfo.Text, drawingInfo.TextPosition,
                                drawingInfo.FontFace, drawingInfo.FontScale, drawingInfo.TextColor, drawingInfo.Thickness);
                }
            }

            if (_processedImage != null && !_processedImage.Empty())
            {
                pictureBoxProcessed.Image = OpenCvSharp.Extensions.BitmapConverter.ToBitmap(_processedImage);
            }
            else
            {
                pictureBoxProcessed.Image = null;
                AppendLogUiThread("Ошибка: Обработанное изображение (_processedImage) пустое или null после попытки перерисовки.");
            }
        }


        private void HideNoChar_CheckedChanged(object sender, EventArgs e)
        {
            // Перерисовываем, только если есть базовое изображение
            // _plateDrawingInfos может быть пустым, RefreshProcessedImageDisplay это обработает.
            if (_scaledBaseImage != null && !_scaledBaseImage.Empty())
            {
                RefreshProcessedImageDisplay();
            }
        }

        private List<Rect> ApplySimplifiedNMS(List<Tuple<Rect, double>> proposals, double overlapThreshold)
        {
            List<Rect> finalDetections = new List<Rect>();
            if (proposals.Count == 0) return finalDetections;

            proposals.Sort((a, b) => b.Item2.CompareTo(a.Item2));
            List<Tuple<Rect, double>> picked = new List<Tuple<Rect, double>>();

            while (proposals.Count > 0)
            {
                var current = proposals[0];
                picked.Add(current);
                proposals.RemoveAt(0);
                proposals.RemoveAll(proposal => CalculateIoU(current.Item1, proposal.Item1) > overlapThreshold);
            }
            finalDetections.AddRange(picked.Select(p => p.Item1));
            return finalDetections;
        }

        private double CalculateIoU(Rect r1, Rect r2)
        {
            int xA = Math.Max(r1.Left, r2.Left);
            int yA = Math.Max(r1.Top, r2.Top);
            int xB = Math.Min(r1.Right, r2.Right);
            int yB = Math.Min(r1.Bottom, r2.Bottom);
            int interArea = Math.Max(0, xB - xA) * Math.Max(0, yB - yA);
            if (interArea == 0) return 0;
            int boxAArea = r1.Width * r1.Height;
            int boxBArea = r2.Width * r2.Height;
            double iou = (double)interArea / (boxAArea + boxBArea - interArea);
            return iou;
        }

        private string RecognizeCharsOnPlate(Mat plateImage)
        {
            if (plateImage.Empty())
            {
                AppendLogUiThread("[RecognizeCharsOnPlate] Входное изображение номера пустое.");
                return "[PLATE_EMPTY]";
            }

            StringBuilder recognizedText = new StringBuilder();
            Mat grayPlate = new Mat();
            Mat preprocessedPlate = new Mat();
            List<Rect> charBoundingRects = new List<Rect>();

            try
            {
                if (plateImage.Channels() > 1)
                    Cv2.CvtColor(plateImage, grayPlate, ColorConversionCodes.BGR2GRAY);
                else
                    plateImage.CopyTo(grayPlate);

                using (var clahe = Cv2.CreateCLAHE(clipLimit: 2.0, tileGridSize: new OpenCvSharp.Size(8, 8)))
                {
                    clahe.Apply(grayPlate, preprocessedPlate);
                }
                Cv2.Threshold(preprocessedPlate, preprocessedPlate, 0, 255, ThresholdTypes.BinaryInv | ThresholdTypes.Otsu);

                OpenCvSharp.Point[][] contours;
                HierarchyIndex[] hierarchy;
                Cv2.FindContours(preprocessedPlate.Clone(), out contours, out hierarchy, RetrievalModes.External, ContourApproximationModes.ApproxSimple);

                if (contours == null || contours.Length == 0)
                {
                    // AppendLogUiThread("[RecognizeCharsOnPlate] Контуры символов не найдены после FindContours.");
                    grayPlate.Dispose(); preprocessedPlate.Dispose();
                    return "[NO_CONTOURS]";
                }

                double plateH = preprocessedPlate.Height;
                double plateW = preprocessedPlate.Width;

                foreach (OpenCvSharp.Point[] contour in contours)
                {
                    if (contour == null || contour.Length < 3) continue;
                    Rect boundingRect = Cv2.BoundingRect(contour);
                    bool hCond = boundingRect.Height > plateH * 0.30 && boundingRect.Height < plateH * 0.95;
                    bool wCond = boundingRect.Width > plateW * 0.03 && boundingRect.Width < plateW * 0.30;
                    double ar = (boundingRect.Height == 0) ? 1000 : (double)boundingRect.Width / boundingRect.Height;
                    bool arCond = ar > 0.1 && ar < 1.2;
                    bool sizeCond = boundingRect.Width > 3 && boundingRect.Height > 7;
                    if (hCond && wCond && arCond && sizeCond)
                    {
                        charBoundingRects.Add(boundingRect);
                    }
                }

                if (charBoundingRects.Count == 0)
                {
                    // AppendLogUiThread("[RecognizeCharsOnPlate] Прямоугольники символов не найдены после фильтрации.");
                    grayPlate.Dispose(); preprocessedPlate.Dispose();
                    return "[NO_CHAR_RECTS]"; // Это ключевая строка для скрытия
                }

                charBoundingRects = charBoundingRects.OrderBy(r => r.X).ToList();

                foreach (Rect charRect in charBoundingRects)
                {
                    Rect safeCharRect = new Rect(
                        Math.Max(0, charRect.X), Math.Max(0, charRect.Y),
                        Math.Min(charRect.Width, preprocessedPlate.Cols - Math.Max(0, charRect.X)),
                        Math.Min(charRect.Height, preprocessedPlate.Rows - Math.Max(0, charRect.Y))
                    );
                    if (safeCharRect.Width <= 0 || safeCharRect.Height <= 0) continue;

                    using (Mat charCandidate = new Mat(preprocessedPlate, safeCharRect))
                    using (Mat resizedChar = new Mat())
                    {
                        if (charCandidate.Empty()) continue;
                        Cv2.Resize(charCandidate, resizedChar, TEMPLATE_CHAR_SIZE, interpolation: InterpolationFlags.Linear);
                        char bestMatchCharOverall = '?';
                        double maxMatchScoreOverall = -1.0;
                        foreach (var charEntry in _charTemplatesMulti)
                        {
                            char currentSymbolKey = charEntry.Key;
                            List<Mat> listOfTemplatesForSymbol = charEntry.Value;
                            if (listOfTemplatesForSymbol == null) continue;
                            double bestScoreForThisSymbolKeyFromVariants = -1.0;
                            foreach (Mat templateVariant in listOfTemplatesForSymbol)
                            {
                                if (templateVariant == null || templateVariant.Empty()) continue;
                                using (Mat result = new Mat())
                                {
                                    Cv2.MatchTemplate(resizedChar, templateVariant, result, TemplateMatchModes.CCoeffNormed);
                                    Cv2.MinMaxLoc(result, out _, out double currentVariantScore, out _, out _);
                                    if (currentVariantScore > bestScoreForThisSymbolKeyFromVariants)
                                    {
                                        bestScoreForThisSymbolKeyFromVariants = currentVariantScore;
                                    }
                                }
                            }
                            if (bestScoreForThisSymbolKeyFromVariants > maxMatchScoreOverall)
                            {
                                maxMatchScoreOverall = bestScoreForThisSymbolKeyFromVariants;
                                bestMatchCharOverall = currentSymbolKey;
                            }
                        }
                        if (maxMatchScoreOverall > 0.50)
                        {
                            recognizedText.Append(bestMatchCharOverall);
                        }
                        else
                        {
                            recognizedText.Append('?');
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                AppendLogUiThread($"[RecognizeCharsOnPlate] Исключение: {ex.Message}\n{ex.StackTrace}");
                return "[OCR_EXCEPTION]";
            }
            finally
            {
                grayPlate.Dispose();
                preprocessedPlate.Dispose();
            }

            if (charBoundingRects.Count > 0 && (recognizedText.Length == 0 || recognizedText.ToString().All(c => c == '?')))
            {
                // AppendLogUiThread("[RecognizeCharsOnPlate] Кандидаты на символы были, но не распознаны или все с низким баллом.");
                return new string('?', Math.Min(charBoundingRects.Count, 8)); // Возвращаем '???' вместо "[NO_CHAR_RECTS]"
            }

            return recognizedText.ToString();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            _originalImage?.Dispose();
            _processedImage?.Dispose();
            _scaledBaseImage?.Dispose(); // Освобождаем _scaledBaseImage
            foreach (var charTemplatesList in _charTemplatesMulti.Values)
            {
                if (charTemplatesList != null)
                {
                    foreach (var tpl in charTemplatesList)
                    {
                        tpl?.Dispose();
                    }
                }
            }
            _charTemplatesMulti.Clear();
            base.OnFormClosing(e);
        }

        private async void btnTrainMLP_Click(object sender, EventArgs e)
        {
            btnLoadImage.Enabled = false;
            btnProcess.Enabled = false;
            if (this.Controls.ContainsKey("btnTrainMLP")) ((Button)this.Controls["btnTrainMLP"]).Enabled = false;
            if (this.Controls.ContainsKey("HideNoChar")) (this.Controls["HideNoChar"] as CheckBox).Enabled = false;


            AppendLogUiThread("\n--- Начало обучения MLP ---");
            if (this.statusStrip1 != null && this.statusStrip1.Items["toolStripStatusLabel"] != null)
                ((ToolStripStatusLabel)this.statusStrip1.Items["toolStripStatusLabel"]).Text = "MLP: Обучение...";
            progressBar.Style = ProgressBarStyle.Marquee;
            progressBar.Value = 0;

            string positiveSamplesDir = @"C:\trasher\app\6sem\Neuro\L1_Nomera\test";
            string negativeSamplesDir = @"C:\trasher\app\6sem\Neuro\L1_Nomera\Negative";

            int epochs = 1500;
            double learningRate = 0.001;

            List<double[]> trainingInputs = new List<double[]>();
            List<double[]> trainingOutputs = new List<double[]>();

            bool trainingSuccess = await Task.Run(() =>
            {
                AppendLogUiThread("Загрузка положительных примеров...");
                LoadAndProcessSamplesForTraining(positiveSamplesDir, 1.0, trainingInputs, trainingOutputs);
                AppendLogUiThread("Загрузка отрицательных примеров...");
                LoadAndProcessSamplesForTraining(negativeSamplesDir, 0.0, trainingInputs, trainingOutputs);

                if (trainingInputs.Count == 0)
                {
                    AppendLogUiThread("Ошибка: Нет данных для обучения! Проверьте пути и наличие изображений.");
                    return false;
                }
                if (trainingInputs.Count < 50)
                {
                    AppendLogUiThread($"Предупреждение: Очень мало обучающих данных ({trainingInputs.Count}). Качество модели может быть низким.");
                }

                AppendLogUiThread("Перемешивание данных...");
                Random rng = new Random();
                int n = trainingInputs.Count;
                for (int i = 0; i < n - 1; i++)
                {
                    int k = rng.Next(i, n);
                    var tempInput = trainingInputs[k]; trainingInputs[k] = trainingInputs[i]; trainingInputs[i] = tempInput;
                    var tempOutput = trainingOutputs[k]; trainingOutputs[k] = trainingOutputs[i]; trainingOutputs[i] = tempOutput;
                }
                AppendLogUiThread("Данные перемешаны.");
                AppendLogUiThread($"Всего подготовлено {trainingInputs.Count} примеров для обучения (размер патча: {MLP_TRAIN_TARGET_PATCH_SIZE}x{MLP_TRAIN_TARGET_PATCH_SIZE}).");

                _plateMlp = new PlateMLPClassifier();
                _plateMlp.Train(trainingInputs, trainingOutputs, epochs: epochs, learningRate: learningRate, logCallback: AppendLogUiThread);

                if (_plateMlp.SaveWeights(MlpWeightsFile))
                {
                    AppendLogUiThread($"Веса MLP успешно сохранены в файл: {Path.GetFullPath(MlpWeightsFile)}");
                    return true;
                }
                else
                {
                    AppendLogUiThread("Ошибка при сохранении весов MLP.");
                    return false;
                }
            });

            if (trainingSuccess)
            {
                AppendLogUiThread("--- Обучение MLP успешно завершено ---");
            }
            else
            {
                AppendLogUiThread("--- Обучение MLP не удалось или было прервано ---");
            }

            progressBar.Style = ProgressBarStyle.Blocks;
            progressBar.Value = progressBar.Maximum;

            btnLoadImage.Enabled = true;
            btnProcess.Enabled = true;
            if (this.Controls.ContainsKey("btnTrainMLP")) ((Button)this.Controls["btnTrainMLP"]).Enabled = true;
            if (this.Controls.ContainsKey("HideNoChar")) (this.Controls["HideNoChar"] as CheckBox).Enabled = true;
            UpdateMlpStatusLabel();
        }

        private void LoadAndProcessSamplesForTraining(string directoryPath, double label, List<double[]> inputs, List<double[]> outputs)
        {
            if (!Directory.Exists(directoryPath))
            {
                AppendLogUiThread($"Папка не найдена: {directoryPath}");
                return;
            }
            string[] imageFiles = Directory.GetFiles(directoryPath, "*.*", SearchOption.TopDirectoryOnly)
                .Where(s => s.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
                            s.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                            s.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ||
                            s.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase)).ToArray();
            AppendLogUiThread($"Найдено {imageFiles.Length} файлов в {Path.GetFileName(directoryPath)}");
            int processedCount = 0;
            foreach (string filePath in imageFiles)
            {
                try
                {
                    using (Mat originalMat = Cv2.ImRead(filePath, ImreadModes.Color))
                    {
                        if (originalMat.Empty()) continue;
                        using (Mat grayMat = new Mat())
                        using (Mat resizedMat = new Mat())
                        {
                            Cv2.CvtColor(originalMat, grayMat, ColorConversionCodes.BGR2GRAY);
                            Cv2.Resize(grayMat, resizedMat, new OpenCvSharp.Size(MLP_TRAIN_TARGET_PATCH_SIZE, MLP_TRAIN_TARGET_PATCH_SIZE), 0, 0, InterpolationFlags.Area);
                            double[] inputVector = new double[MLP_TRAIN_TARGET_PATCH_SIZE * MLP_TRAIN_TARGET_PATCH_SIZE];
                            int k = 0;
                            for (int i = 0; i < resizedMat.Rows; i++)
                            {
                                for (int j = 0; j < resizedMat.Cols; j++)
                                {
                                    inputVector[k++] = resizedMat.Get<byte>(i, j) / 255.0;
                                }
                            }
                            inputs.Add(inputVector);
                            outputs.Add(new double[] { label });
                            processedCount++;
                        }
                    }
                }
                catch (Exception ex)
                {
                    AppendLogUiThread($"Ошибка при обработке файла {filePath} для обучения: {ex.Message}");
                }
            }
            AppendLogUiThread($"Успешно обработано {processedCount} файлов из {Path.GetFileName(directoryPath)} для обучения.");
        }

        private void AppendLogUiThread(string message)
        {
            if (txtLog.InvokeRequired)
            {
                txtLog.Invoke(new Action(() => AppendLogUiThread(message)));
            }
            else
            {
                if (txtLog.Text.Length > 30000)
                {
                    txtLog.Text = txtLog.Text.Substring(txtLog.Text.Length - 15000);
                }
                txtLog.AppendText(message + Environment.NewLine);
                txtLog.SelectionStart = txtLog.Text.Length;
                txtLog.ScrollToCaret();
            }
        }
    }
}