#pragma once

#include <QCheckBox>
#include <QDialog>
#include <QDialogButtonBox>
#include <QDir>
#include <QFileDialog>
#include <QFormLayout>
#include <QHBoxLayout>
#include <QJsonObject>
#include <QLabel>
#include <QLineEdit>
#include <QPushButton>
#include <QSpinBox>
#include <functional>

class OptionsDialog final : public QDialog
{
public:
    using Save = std::function<void(const QJsonObject &)>;

    OptionsDialog(QWidget *parent, const QJsonObject &options, Save save)
        : QDialog(parent), original_(options)
    {
        setWindowTitle(tr("Options"));
        setMinimumWidth(460);
        auto *form = new QFormLayout(this);
        auto *folder = new QWidget(this);
        auto *folderLayout = new QHBoxLayout(folder);
        folderLayout->setContentsMargins(0, 0, 0, 0);
        directory_ = new QLineEdit(options.value("defaultDownloadDirectory").toString(), folder);
        auto *browse = new QPushButton(tr("Browse…"), folder);
        folderLayout->addWidget(directory_, 1);
        folderLayout->addWidget(browse);
        form->addRow(tr("Default download folder"), folder);
        connect(browse, &QPushButton::clicked, this, [this] {
            const auto path = QFileDialog::getExistingDirectory(this, tr("Download folder"), directory_->text());
            if (!path.isEmpty()) directory_->setText(path);
        });
        parallel_ = new QSpinBox(this);
        parallel_->setRange(1, 16);
        parallel_->setValue(options.value("maxConcurrentDownloads").toInt(3));
        form->addRow(tr("Simultaneous downloads"), parallel_);
        connections_ = new QSpinBox(this);
        connections_->setRange(1, 32);
        connections_->setValue(options.value("defaultConnections").toInt(8));
        form->addRow(tr("Default connections"), connections_);
        limit_ = new QLineEdit(QString::number(options.value("globalSpeedLimitBytesPerSecond").toInteger()), this);
        form->addRow(tr("Global limit in bytes/s (0 = unlimited)"), limit_);

        const auto checkbox = [this, form, options](const QString &text, const QString &key) {
            auto *control = new QCheckBox(text, this);
            control->setChecked(options.value(key).toBool());
            form->addRow(control);
            return control;
        };
        notify_ = checkbox(tr("Notify when a download finishes"), "notifyOnCompletion");
        completion_ = checkbox(tr("Show the completion dialog"), "showCompletionDialog");
        resume_ = checkbox(tr("Resume unfinished downloads when Inbound starts"), "autoResumeUnfinishedOnStartup");
        notice_ = new QLabel(this);
        notice_->setTextFormat(Qt::PlainText);
        notice_->setWordWrap(true);
        form->addRow(notice_);
        buttons_ = new QDialogButtonBox(QDialogButtonBox::Save | QDialogButtonBox::Cancel, this);
        form->addRow(buttons_);
        connect(buttons_, &QDialogButtonBox::rejected, this, &QDialog::reject);
        connect(buttons_, &QDialogButtonBox::accepted, this, [this, save] {
            bool valid = false;
            const auto limit = limit_->text().trimmed().toLongLong(&valid);
            const auto directory = directory_->text().trimmed();
            if (!valid || limit < 0 || !QDir::isAbsolutePath(directory)) {
                notice_->setText(tr("Enter an absolute download folder and a non-negative whole-number speed limit."));
                return;
            }
            QJsonObject changes;
            const auto addChange = [&](const QString &key, const QJsonValue &value) {
                if (original_.value(key) != value) changes.insert(key, value);
            };
            addChange("defaultDownloadDirectory", directory);
            addChange("maxConcurrentDownloads", parallel_->value());
            addChange("defaultConnections", connections_->value());
            addChange("globalSpeedLimitBytesPerSecond", limit);
            addChange("notifyOnCompletion", notify_->isChecked());
            addChange("showCompletionDialog", completion_->isChecked());
            addChange("autoResumeUnfinishedOnStartup", resume_->isChecked());
            if (changes.isEmpty()) { accept(); return; }
            buttons_->setEnabled(false);
            notice_->setText(tr("Saving…"));
            save(changes);
        });
    }

    void saveResult(const QJsonObject &result)
    {
        if (result.value("ok").toBool()) {
            accept();
            return;
        }
        notice_->setText(result.value("error").toString(tr("The Host could not save the options.")));
        buttons_->setEnabled(true);
    }

private:
    QJsonObject original_;
    QLineEdit *directory_;
    QLineEdit *limit_;
    QSpinBox *parallel_;
    QSpinBox *connections_;
    QCheckBox *notify_;
    QCheckBox *completion_;
    QCheckBox *resume_;
    QLabel *notice_;
    QDialogButtonBox *buttons_;
};
