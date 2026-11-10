#pragma once

#include <QCheckBox>
#include <QComboBox>
#include <QDialog>
#include <QDialogButtonBox>
#include <QFormLayout>
#include <QHBoxLayout>
#include <QJsonArray>
#include <QJsonObject>
#include <QTimer>
#include <QJsonValue>
#include <QLabel>
#include <QLineEdit>
#include <QListWidget>
#include <QMessageBox>
#include <QStringList>
#include <QPushButton>
#include <QSpinBox>
#include <QVBoxLayout>
#include <functional>

// Standalone Qt 6 Widgets TorBox workflow. The owner supplies the IPC bridge;
// every outbound payload is a JSON object and API keys are never displayed after save.
class TorBoxDialog final : public QDialog {
public:
    using CommandHandler = std::function<void(const QString&, const QJsonObject&)>;
    explicit TorBoxDialog(QWidget* parent, CommandHandler send)
        : QDialog(parent), send_(std::move(send)) {
        setWindowTitle(tr("TorBox"));
        resize(620, 560);
        auto* root = new QVBoxLayout(this);
        auto* accountForm = new QFormLayout;
        status_ = new QLabel(tr("Account status has not been loaded."), this);
        status_->setWordWrap(true);
        status_->setTextFormat(Qt::PlainText);
        accountForm->addRow(tr("Account"), status_);
        auto* keyRow = new QHBoxLayout;
        key_ = new QLineEdit(this);
        key_->setEchoMode(QLineEdit::Password);
        key_->setPlaceholderText(tr("API key (hidden)"));
        key_->setAccessibleName(tr("TorBox API key"));
        auto* setKey = new QPushButton(tr("Set key"), this);
        auto* clearKey = new QPushButton(tr("Clear key"), this);
        keyRow->addWidget(key_, 1); keyRow->addWidget(setKey); keyRow->addWidget(clearKey);
        accountForm->addRow(tr("API key"), keyRow);
        root->addLayout(accountForm);
        auto* refreshAccount = new QPushButton(tr("Refresh account"), this);
        root->addWidget(refreshAccount, 0, Qt::AlignLeft);

        auto* catalog = new QFormLayout;
        kind_ = new QComboBox(this);
        kind_->addItem(tr("Torrents"), QStringLiteral("torrents"));
        kind_->addItem(tr("Web downloads"), QStringLiteral("webdl"));
        kind_->addItem(tr("Usenet"), QStringLiteral("usenet"));
        offset_ = new QSpinBox(this); offset_->setRange(0, 100000000); offset_->setSingleStep(50);
        fresh_ = new QCheckBox(tr("Fetch fresh data"), this);
        catalog->addRow(tr("Catalog kind"), kind_);
        catalog->addRow(tr("Offset"), offset_);
        catalog->addRow(QString(), fresh_);
        root->addLayout(catalog);
        auto* pageButtons = new QHBoxLayout;
        auto* load = new QPushButton(tr("Load page"), this);
        auto* previous = new QPushButton(tr("Previous"), this);
        auto* next = new QPushButton(tr("Next"), this);
        previous->setEnabled(false);
        next->setEnabled(false);
        next_ = next;
        pageButtons->addWidget(previous); pageButtons->addWidget(load); pageButtons->addWidget(next);
        root->addLayout(pageButtons);
        items_ = new QListWidget(this);
        items_->setSelectionMode(QAbstractItemView::SingleSelection);
        root->addWidget(items_, 1);

        auto* addForm = new QFormLayout;
        input_ = new QLineEdit(this);
        input_->setPlaceholderText(tr("Magnet link or TorBox item URL (optional)"));
        destination_ = new QLineEdit(this);
        destination_->setPlaceholderText(tr("Optional destination folder"));
        approve_ = new QCheckBox(tr("I approve uncached items (may consume TorBox resources)"), this);
        addForm->addRow(tr("Direct input"), input_);
        addForm->addRow(tr("Destination"), destination_);
        addForm->addRow(QString(), approve_);
        root->addLayout(addForm);
        auto* actions = new QHBoxLayout;
        auto* addSelected = new QPushButton(tr("Choose Files…"), this);
        auto* submitLink = new QPushButton(tr("Submit link"), this);
        auto* close = new QPushButton(tr("Close"), this);
        actions->addWidget(addSelected); actions->addWidget(submitLink); actions->addStretch(); actions->addWidget(close);
        root->addLayout(actions);
        message_ = new QLabel(this); message_->setWordWrap(true); message_->setTextFormat(Qt::PlainText); root->addWidget(message_);

        connect(refreshAccount, &QPushButton::clicked, this, [this] { request(QStringLiteral("torbox-account"), {{QStringLiteral("refresh"), true}}); });
        connect(setKey, &QPushButton::clicked, this, [this] {
            const QString value = key_->text();
            if (value.trimmed().isEmpty()) { message_->setText(tr("Enter an API key first.")); return; }
            request(QStringLiteral("torbox-set-key"), {{QStringLiteral("key"), value}});
            key_->clear();
        });
        connect(clearKey, &QPushButton::clicked, this, [this] {
            if (QMessageBox::question(this, tr("Clear TorBox key?"), tr("Remove the saved TorBox key?"),
                    QMessageBox::Yes | QMessageBox::No, QMessageBox::No) == QMessageBox::Yes) {
                key_->clear();
                request(QStringLiteral("torbox-clear-key"), {});
            }
        });
        connect(load, &QPushButton::clicked, this, [this] { loadPage(); });
        connect(previous, &QPushButton::clicked, this, [this] { offset_->setValue(qMax(0, offset_->value() - pageSize_)); loadPage(); });
        connect(next, &QPushButton::clicked, this, [this] { offset_->setValue(offset_->value() + pageSize_); loadPage(); });
        connect(offset_, &QSpinBox::valueChanged, previous, [previous](int value) { previous->setEnabled(value > 0); });
        connect(kind_, &QComboBox::currentTextChanged, this, [this] {
            offset_->setValue(0);
            items_->clear();
            next_->setEnabled(false);
        });
        connect(addSelected, &QPushButton::clicked, this, [this] { addChosen(); });
        connect(submitLink, &QPushButton::clicked, this, [this] { submit(); });
        connect(close, &QPushButton::clicked, this, &QDialog::accept);
        QTimer::singleShot(0, this, [this] { request(QStringLiteral("torbox-account"), {}); });
    }

    void handleResult(const QString &command, const QJsonObject &result)
    {
        setEnabled(true);
        if (!result.value("ok").toBool()) {
            message_->setText(result.value("error").toString(tr("The Host could not complete this TorBox request.")));
            return;
        }
        if (command == "torbox-account" || command == "torbox-set-key" || command == "torbox-clear-key") {
            const auto accounts = result.value("accounts").toArray();
            const auto account = accounts.isEmpty() ? QJsonObject{} : accounts.first().toObject();
            const auto plan = account.value("plan").toString();
            status_->setText(account.value("keyUnreadable").toBool() ? tr("Saved key is unreadable")
                : !account.value("hasKey").toBool() ? tr("No key saved")
                : plan.isEmpty() ? tr("Key saved") : tr("Key saved · %1").arg(plan));
            if (command == "torbox-clear-key") {
                items_->clear();
                next_->setEnabled(false);
            }
            message_->clear();
        } else if (command == "torbox-catalog") {
            const auto rows = result.value("items").toArray();
            items_->clear();
            for (const auto value : rows) {
                const auto item = value.toObject();
                const auto id = item.value("itemId").toString();
                if (id.isEmpty()) continue;
                const auto label = item.value("name").toString(id);
                auto *row = new QListWidgetItem(label, items_);
                row->setData(Qt::UserRole, id);
            }
            next_->setEnabled(result.value("hasMore").toBool());
            message_->setText(rows.isEmpty() ? tr("No items on this page.") : tr("Loaded %1 items.").arg(rows.size()));
        } else if (command == "torbox-item") {
            const auto entries = result.value("items").toArray();
            if (entries.isEmpty()) {
                message_->setText(tr("The catalog item is no longer available."));
                return;
            }
            chooseFiles(entries.first().toObject());
        } else {
            message_->setText(result.value("note").toString(tr("Download request accepted.")));
        }
    }

private:
    void request(const QString &command, const QJsonObject &payload)
    {
        setEnabled(false);
        message_->setText(tr("Waiting for Host…"));
        send_(command, payload);
    }

    void loadPage()
    {
        items_->clear();
        next_->setEnabled(false);
        request("torbox-catalog", {{"kind", kind_->currentData().toString()},
                                  {"offset", offset_->value()}, {"fresh", fresh_->isChecked()}});
    }

    void addChosen()
    {
        const auto selected = items_->selectedItems();
        if (selected.isEmpty()) { message_->setText(tr("Select a catalog item first.")); return; }
        request("torbox-item", {{"kind", kind_->currentData().toString()},
                                {"itemId", selected.front()->data(Qt::UserRole).toString()}});
    }

    void chooseFiles(const QJsonObject &item)
    {
        QDialog dialog(this);
        dialog.setWindowTitle(tr("Choose TorBox Files"));
        dialog.resize(510, 420);
        auto *layout = new QVBoxLayout(&dialog);
        auto *name = new QLabel(item.value("name").toString(), &dialog);
        name->setTextFormat(Qt::PlainText);
        name->setWordWrap(true);
        layout->addWidget(name);
        auto *list = new QListWidget(&dialog);
        list->setAccessibleName(tr("Files to download"));
        for (const auto value : item.value("files").toArray()) {
            const auto file = value.toObject();
            const auto id = file.value("fileId").toString();
            if (id.isEmpty() || !file.value("selectable").toBool() || file.value("infected").toBool()) continue;
            auto *entry = new QListWidgetItem(file.value("relativePath").toString(id), list);
            entry->setData(Qt::UserRole, id);
            entry->setFlags(entry->flags() | Qt::ItemIsUserCheckable);
            entry->setCheckState(Qt::Checked);
        }
        if (list->count() == 0) {
            message_->setText(tr("This item has no selectable files yet."));
            return;
        }
        layout->addWidget(list);
        auto *buttons = new QDialogButtonBox(QDialogButtonBox::Cancel, &dialog);
        auto *download = buttons->addButton(tr("Download"), QDialogButtonBox::AcceptRole);
        connect(list, &QListWidget::itemChanged, download, [list, download] {
            bool checked = false;
            for (int row = 0; row < list->count(); ++row)
                checked |= list->item(row)->checkState() == Qt::Checked;
            download->setEnabled(checked);
        });
        connect(buttons, &QDialogButtonBox::accepted, &dialog, &QDialog::accept);
        connect(buttons, &QDialogButtonBox::rejected, &dialog, &QDialog::reject);
        layout->addWidget(buttons);
        if (dialog.exec() != QDialog::Accepted) return;
        QJsonArray ids;
        for (int row = 0; row < list->count(); ++row)
            if (list->item(row)->checkState() == Qt::Checked)
                ids.append(list->item(row)->data(Qt::UserRole).toString());
        QJsonObject payload{{"itemKind", item.value("kind")}, {"itemId", item.value("itemId")}, {"fileIds", ids}};
        if (!destination_->text().trimmed().isEmpty())
            payload.insert("destination", destination_->text().trimmed());
        request("torbox-add", payload);
    }

    void submit() {
        const QString input = input_->text().trimmed();
        if (input.isEmpty()) { message_->setText(tr("Enter a link first.")); return; }
        if (approve_->isChecked()) {
            const auto answer = QMessageBox::question(this, tr("Approve uncached submission?"),
                tr("This explicitly approves submitting an uncached link to TorBox, which may consume provider resources."),
                QMessageBox::Yes | QMessageBox::Cancel, QMessageBox::Cancel);
            if (answer != QMessageBox::Yes) return;
        }
        QJsonObject payload{{QStringLiteral("input"), input}, {QStringLiteral("approveUncached"), approve_->isChecked()}};
        if (!destination_->text().trimmed().isEmpty()) payload.insert(QStringLiteral("destination"), destination_->text().trimmed());
        request(QStringLiteral("torbox-add"), payload);
        input_->clear();
    }
    CommandHandler send_;
    QLabel *status_{}, *message_{};
    QLineEdit *key_{}, *input_{}, *destination_{};
    QComboBox* kind_{};
    QSpinBox* offset_{};
    QCheckBox *fresh_{}, *approve_{};
    QListWidget* items_{};
    int pageSize_ = 50;
    QPushButton *next_ = nullptr;
};
