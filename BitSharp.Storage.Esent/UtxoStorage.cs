﻿using BitSharp.Common;
using BitSharp.Common.ExtensionMethods;
using BitSharp.Data;
using Microsoft.Isam.Esent.Collections.Generic;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace BitSharp.Storage.Esent
{
    public class UtxoStorage : IUtxoStorage
    {
        private readonly UInt256 blockHash;
        private readonly string directory;
        private PersistentByteDictionary utxo;
        private readonly ReaderWriterLockSlim utxoLock;

        static internal string GetDirectory(UInt256 blockHash)
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BitSharp", "utxo", blockHash.ToString());
        }

        internal UtxoStorage(UInt256 blockHash)
        {
            this.blockHash = blockHash;
            this.directory = GetDirectory(blockHash);
            this.utxo = new PersistentByteDictionary(this.directory);
            this.utxoLock = new ReaderWriterLockSlim();
        }

        public UInt256 BlockHash
        {
            get { return this.blockHash; }
        }

        public int Count
        {
            get { return this.utxoLock.DoRead(() => this.utxo.Count); }
        }

        public IEnumerable<UnspentTx> UnspentTransactions()
        {
            this.utxoLock.EnterReadLock();
            try
            {
                foreach (var rawUnspentTx in utxo)
                {
                    yield return StorageEncoder.DecodeUnspentTx(new UInt256(rawUnspentTx.Key), rawUnspentTx.Value);
                }
            }
            finally
            {
                this.utxoLock.ExitReadLock();
            }
        }

        public bool ContainsKey(Common.UInt256 txHash)
        {
            return this.utxoLock.DoRead(() => utxo.ContainsKey(txHash.ToByteArray()));
        }

        public bool TryGetValue(UInt256 txHash, out UnspentTx unspentTx)
        {
            UnspentTx unspentTxLocal = null;
            this.utxoLock.DoRead(
                () =>
                {
                    byte[] unspentTxBytes;
                    if (this.utxo.TryGetValue(txHash.ToByteArray(), out unspentTxBytes))
                    {
                        unspentTxLocal = StorageEncoder.DecodeUnspentTx(txHash, unspentTxBytes);
                    }
                });

            unspentTx = unspentTxLocal;
            return unspentTxLocal != null;
        }

        internal void Duplicate(string destDirectory)
        {
            this.utxoLock.DoWrite(() =>
            {
                this.utxo.Dispose();

                Directory.CreateDirectory(destDirectory);
                foreach (var srcFile in Directory.GetFiles(this.directory, "*.edb"))
                    File.Copy(srcFile, Path.Combine(destDirectory, Path.GetFileName(srcFile)));

                this.utxo = new PersistentByteDictionary(this.directory);
            });
        }

        public void Dispose()
        {
            this.utxo.Dispose();
        }

        public void DisposeDelete()
        {
            this.Dispose();
            Directory.Delete(this.directory, recursive: true);
        }
    }
}
