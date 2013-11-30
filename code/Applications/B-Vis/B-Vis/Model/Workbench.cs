﻿
using Bevisuali.Util;
using FAI.Bayesian;
using Microsoft.FSharp.Core;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;

namespace Bevisuali.Model
{
    internal partial class Workbench : IWorkbench
    {
        public Workbench()
        {
            _bayesianNetwork = new BayesianNetwork("Empty");
            _bayesianNetworkVariableAbbreviations = new Dictionary<string, string>();

            _scenarios = new ObservableCollection<IScenario>();
            _scenarios.CollectionChanged += ScenariosChanged;
            _scenariosInternal = new List<ScenarioRecord>();

            _scenariosThreadCancel = false;
            _scenariosThread = new Thread(ThreadMainScenariosInference);
            _scenariosThread.Name = "Inference";
            _scenariosThread.Start();

            _learningTasks = new ObservableCollection<ILearningTask>();
            _learningTasks.CollectionChanged += LearningTasksChanged;
            _learningTasksInternal = new List<LearningTaskRecord>();

            _learningTasksThreadCancel = false;
            _learningTasksThread = new Thread(ThreadMainLearningTasks);
            _learningTasksThread.Name = "Learning";
            _learningTasksThread.Start();

            //NetworkLayoutAlgorithm = Model.NetworkLayoutAlgorithm.CompoundFDP;
            NetworkLayoutAlgorithm = Model.NetworkLayoutAlgorithm.SugiyamaEfficient;
            _networkLayout = new NetworkLayout();
            _networkLayoutInternal = new NetworkLayoutRecord(_bayesianNetwork, _networkLayout, this.NetworkLayoutAlgorithm);

            _networkLayoutThreadCancel = false;
            _networkLayoutThread = new Thread(ThreadMainNetworkLayout);
            _networkLayoutThread.Name = "Layout";
            _networkLayoutThread.Start();
        }
        public void Dispose()
        {
            _scenariosThreadCancel = true;
            _learningTasksThreadCancel = true;
            _networkLayoutThreadCancel = true;

            _scenariosThread.Join(1000);
            _scenariosThread.Abort();
            _learningTasksThread.Join(1000);
            _learningTasksThread.Abort();
            _networkLayoutThread.Join(1000);
        }

        public string Id { get; set; }

        public IObservationSet _dataSet;
        public IObservationSet DataSet
        {
            get
            {
                return _dataSet;
            }
            set
            {
                _dataSet = value;
            }
        }
        
        protected string _selectedVariable;
        public string SelectedVariable
        {
            get
            {
                return _selectedVariable;
            }
            set
            {
                string oldValue = _selectedVariable;
                string newValue;
                if (value == "")
                {
                    newValue = null;
                }
                else
                {
                    newValue = value;
                }

                _selectedVariable = newValue;

                if (oldValue != newValue
                    && SelectedVariableUpdated != null)
                {
                    SelectedVariableUpdated(this);
                }
            }
        }
        protected Mode _selectedVariableMode;
        public Mode SelectedVariableMode
        {
            get
            {
                return _selectedVariableMode;
            }
            set
            {
                var oldValue = _selectedVariableMode;
                var newValue = value;
                _selectedVariableMode = newValue;
                if (oldValue != newValue
                    && SelectedVariableModeUpdated != null)
                {
                    SelectedVariableModeUpdated(this);
                }
            }
        }
        public event Action<IWorkbench> SelectedVariableUpdated;
        public event Action<IWorkbench> SelectedVariableModeUpdated;

        protected BayesianNetwork _bayesianNetwork;
        public BayesianNetwork BayesianNetwork
        {
            get
            {
                return _bayesianNetwork;
            }
            set
            {
                if (value == null)
                {
                    throw new ArgumentNullException();
                }

                bool isNew = value != _bayesianNetwork;

                if (isNew)
                {
                    // Remove old handler.
                    _bayesianNetwork.StructureChanged -= BayesianNetworkStructureChanged;

                    // Remember new network.
                    _bayesianNetwork = value;

                    // Register new handler.
                    _bayesianNetwork.StructureChanged += BayesianNetworkStructureChanged;

                    // Clear evidence.
                    Scenarios.Clear();

                    // Reset layout.
                    _networkLayout = new NetworkLayout();

                    // Initialize layout record.
                    _networkLayoutInternal = new NetworkLayoutRecord(
                        value,
                        _networkLayout,
                        this.NetworkLayoutAlgorithm);

                    // Build abbreviations.
                    {
                        _bayesianNetworkVariableAbbreviations = new Dictionary<string, string>();

                        // Add variables that have numerical suffixes first.
                        Regex regex = new Regex(@"^.*[^0-9]+([0-9]{1,2})$");
                        foreach (var variableName in value.Variables.Select(rv=>rv.Key).OrderBy(n=>n))
                        {
                            var match = regex.Match(variableName);
                            if(!match.Success)
                            {
                                continue;
                            }
                            var capture = match.Groups[1];
                            var letter = variableName[0];
                            var number = ushort.Parse(capture.Value);
                            for (int i = 0; i < 100; ++i)
                            {
                                var abbrev = letter + "_" + number;
                                if (_bayesianNetworkVariableAbbreviations.ContainsValue(abbrev))
                                {
                                    number++;
                                    continue;
                                }
                                _bayesianNetworkVariableAbbreviations[variableName] = abbrev;
                                break;
                            }
                        }

                        var variablesByFirstChar = value.Variables.Select(kvp => kvp.Value).GroupBy(rv => rv.Name[0]);
                        foreach (var charGroup in variablesByFirstChar)
                        {
                            int groupCount = charGroup.Count();

                            if(groupCount == 1 && !_bayesianNetworkVariableAbbreviations.ContainsKey(charGroup.First().Name))
                            {
                                _bayesianNetworkVariableAbbreviations[charGroup.First().Name] = charGroup.Key.ToString();
                            }

                            int number = 1;
                            foreach(var rv in charGroup)
                            {
                                string variableName = rv.Name;
                                if (_bayesianNetworkVariableAbbreviations.ContainsKey(variableName))
                                {
                                    continue;
                                }

                                char letter = rv.Name[0];
                                for (int i = 0; i < 100; ++i)
                                {
                                    var abbrev = letter + "_" + number;
                                    if (_bayesianNetworkVariableAbbreviations.ContainsValue(abbrev))
                                    {
                                        number++;
                                        continue;
                                    }
                                    _bayesianNetworkVariableAbbreviations[variableName] = abbrev;
                                    break;
                                }
                            }
                        }
                    }

                    // Notify.
                    if (BayesianNetworkReplaced != null)
                    {
                        BayesianNetworkReplaced(this);
                    }
                }
            }
        }
        public event Action<IWorkbench> BayesianNetworkReplaced;

        Dictionary<string, string> _bayesianNetworkVariableAbbreviations;
        public IDictionary<string, string> BayesianNetworkVariableAbbreviations
        {
            get
            {
                return _bayesianNetworkVariableAbbreviations;
            }
        }

        protected void BayesianNetworkStructureChanged(object sender, BayesianNetwork network)
        {
            // Initialize new layout record to restart layout process.
            _networkLayoutInternal = new NetworkLayoutRecord(
                network,
                _networkLayout,
                this.NetworkLayoutAlgorithm);
        }
    }
}
