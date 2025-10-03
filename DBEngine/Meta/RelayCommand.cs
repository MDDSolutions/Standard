using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;

namespace MDDDataAccess
{
    public class RelayCommand : ICommand
    {
        private readonly Action execute;
        private readonly Func<bool> canExecute;
        private event EventHandler canExecuteChanged;
        public RelayCommand(Action execute, Func<bool> canExecute = null)
        {
            this.execute = execute ?? throw new ArgumentNullException(nameof(execute));
            this.canExecute = canExecute;
        }
        public bool CanExecute(object parameter) => canExecute == null || canExecute();
        public void Execute(object parameter) => execute();
        public event EventHandler CanExecuteChanged
        {
            add
            {
                if (canExecute != null)
                {
                    canExecuteChanged += value;
                }
            }
            remove
            {
                if (canExecute != null)
                {
                    canExecuteChanged -= value;
                }
            }
        }
        public void RaiseCanExecuteChanged()
        {
            canExecuteChanged?.Invoke(this, EventArgs.Empty);
        }
    }


    public sealed class AsyncDbCommand : ICommand
    {
        private readonly Func<DBEngine, Task<bool>> _execute;
        private readonly Func<bool> _canExecute;
        private readonly Action<Exception> _onError;
        private bool _isExecuting;

        public AsyncDbCommand(
            Func<DBEngine, Task<bool>> execute,
            Func<bool> canExecute = null,
            Action<Exception> onError = null)
        {
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            _canExecute = canExecute;
            _onError = onError;
        }

        public bool IsExecuting => _isExecuting;

        public bool CanExecute(object parameter)
        {
            if (_isExecuting) return false;
            if (_canExecute != null && !_canExecute()) return false;
            // Require a DBEngine parameter (or allow null if you prefer)
            return true; // parameter is DBEngine;
        }
        public void Execute (object parameter)
        {
            throw new NotImplementedException();
        }
        public async Task<bool> ExecuteAsync(object parameter)
        {
            if (!CanExecute(parameter))
                return false;

            _isExecuting = true;
            RaiseCanExecuteChanged();

            try
            {
                return await _execute(parameter as DBEngine).ConfigureAwait(false);
            }
            //catch (Exception ex)
            //{
            //    // Surface errors somewhere sensible (log/UI)
            //    if (_onError != null) _onError(ex);
            //    else System.Diagnostics.Debug.WriteLine(ex);
            //    return false;
            //}
            finally
            {
                _isExecuting = false;
                RaiseCanExecuteChanged();
            }
        }

        public event EventHandler CanExecuteChanged;

        public void RaiseCanExecuteChanged() =>
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
    public sealed class DbCommand : ICommand
    {
        private readonly Action<DBEngine> _execute;
        private readonly Func<bool> _canExecute;

        public DbCommand(Action<DBEngine> execute, Func<bool> canExecute = null)
        {
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            _canExecute = canExecute;
        }

        public bool CanExecute(object parameter) =>
            (_canExecute?.Invoke() ?? true); // && parameter is DBEngine;

        public void Execute(object parameter)
        {
            if (parameter is DBEngine db) _execute(db);
        }

        public event EventHandler CanExecuteChanged;
        public void RaiseCanExecuteChanged() =>
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }


}
