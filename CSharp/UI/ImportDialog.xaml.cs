using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using CatchmentTool.Services;

namespace CatchmentTool.UI
{
    public partial class ImportDialog : Window
    {
        private Document _doc;
        private Database _db;
        private List<SurfaceInfo> _surfaces;
        private List<NetworkInfo> _networks;
        
        public ImportDialog(Document doc, string defaultPath = null)
        {
            InitializeComponent();
            _doc = doc;
            _db = doc.Database;
            
            LoadSurfaces();
            LoadNetworks();
            
            if (!string.IsNullOrEmpty(defaultPath))
            {
                txtCatchmentsFile.Text = defaultPath;
            }
            else
            {
                // Try to find a recent catchments.json
                string[] commonPaths = new[]
                {
                    @"C:\Temp\CatchmentData\catchments.json",
                    @"C:\Temp\catchments.json"
                };
                foreach (var path in commonPaths)
                {
                    if (File.Exists(path))
                    {
                        txtCatchmentsFile.Text = path;
                        break;
                    }
                }
            }
        }
        
        private void LoadSurfaces()
        {
            _surfaces = new List<SurfaceInfo>();
            var civilDoc = CivilApplication.ActiveDocument;
            var surfaceIds = civilDoc.GetSurfaceIds();
            
            using (var tr = _db.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in surfaceIds)
                {
                    var surface = tr.GetObject(id, OpenMode.ForRead) as Autodesk.Civil.DatabaseServices.Surface;
                    if (surface != null)
                    {
                        string type = surface is TinSurface ? "TIN" : "Other";
                        _surfaces.Add(new SurfaceInfo 
                        { 
                            Id = id, 
                            Name = surface.Name,
                            DisplayName = $"{surface.Name} ({type})"
                        });
                    }
                }
                tr.Commit();
            }
            
            cmbSurface.ItemsSource = _surfaces;
            cmbSurface.DisplayMemberPath = "DisplayName";
            if (_surfaces.Count > 0) cmbSurface.SelectedIndex = 0;
        }
        
        private void LoadNetworks()
        {
            _networks = new List<NetworkInfo>();
            var civilDoc = CivilApplication.ActiveDocument;
            var networkIds = civilDoc.GetPipeNetworkIds();
            
            using (var tr = _db.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in networkIds)
                {
                    var network = tr.GetObject(id, OpenMode.ForRead) as Network;
                    if (network != null)
                    {
                        int structs = network.GetStructureIds().Count;
                        _networks.Add(new NetworkInfo
                        {
                            Id = id,
                            Name = network.Name,
                            DisplayName = $"{network.Name} ({structs} structures)"
                        });
                    }
                }
                tr.Commit();
            }
            
            cmbNetwork.ItemsSource = _networks;
            cmbNetwork.DisplayMemberPath = "DisplayName";
            if (_networks.Count > 0) cmbNetwork.SelectedIndex = 0;
        }
        
        private void BtnBrowseFile_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Select Catchments File",
                Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*",
                InitialDirectory = File.Exists(txtCatchmentsFile.Text) 
                    ? Path.GetDirectoryName(txtCatchmentsFile.Text) 
                    : @"C:\Temp"
            };
            
            if (dialog.ShowDialog() == true)
            {
                txtCatchmentsFile.Text = dialog.FileName;
            }
        }
        
        private void BtnImport_Click(object sender, RoutedEventArgs e)
        {
            string catchmentsFile = txtCatchmentsFile.Text;
            
            if (string.IsNullOrEmpty(catchmentsFile) || !File.Exists(catchmentsFile))
            {
                MessageBox.Show("Please select a valid catchments file.", "Error", 
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            
            if (cmbNetwork.SelectedItem == null)
            {
                MessageBox.Show("Please select a pipe network.", "Error", 
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            
            if (cmbSurface.SelectedItem == null)
            {
                MessageBox.Show("Please select a surface.", "Error", 
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            
            var networkId = ((NetworkInfo)cmbNetwork.SelectedItem).Id;
            var surfaceId = ((SurfaceInfo)cmbSurface.SelectedItem).Id;
            
            btnImport.IsEnabled = false;
            txtLog.Clear();
            
            try
            {
                RunImport(catchmentsFile, networkId, surfaceId);
            }
            catch (System.Exception ex)
            {
                Log($"ERROR: {ex.Message}");
                MessageBox.Show($"Error: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                btnImport.IsEnabled = true;
            }
        }
        
        private void RunImport(string catchmentsFile, ObjectId networkId, ObjectId surfaceId)
        {
            Log($"Importing from: {catchmentsFile}");
            Log("");
            
            var creator = new CatchmentCreator(_doc);
            var result = creator.ImportCatchments(catchmentsFile, networkId, surfaceId);
            
            Log("");
            Log("=== IMPORT COMPLETE ===");
            Log($"Catchments created: {result.CatchmentsCreated}");
            Log($"Linked to structures: {result.CatchmentsLinked}");
            
            if (result.Errors.Count > 0)
            {
                Log($"Warnings: {result.Errors.Count}");
            }
            
            MessageBox.Show(
                $"Import complete!\n\n" +
                $"Catchments created: {result.CatchmentsCreated}\n" +
                $"Linked to structures: {result.CatchmentsLinked}",
                "Import Complete", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        
        private void Log(string message)
        {
            txtLog.AppendText(message + Environment.NewLine);
            txtLog.ScrollToEnd();
        }
        
        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }
    }
}
